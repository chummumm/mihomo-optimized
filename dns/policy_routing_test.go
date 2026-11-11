package dns

import (
	"context"
	"fmt"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"
	RC "github.com/metacubex/mihomo/rules/common"

	D "github.com/miekg/dns"
)

func policyRoutingTestConfig() Config {
	config := nativeRoutingTestConfig()
	config.Policy = []Policy{{Domain: "+.private.example", NameServers: []NameServer{{Addr: "203.0.113.53:53"}}}}
	config.Fallback = []NameServer{{Addr: "192.0.2.54:53"}}
	config.FallbackIPFilter = []C.IpMatcher{routingTestIPFilter{}}
	config.FallbackLazyQuery = true
	return config
}

func TestDNSRuleRoutingPolicySelectsEffectivePool(t *testing.T) {
	routingTestEnable(t)
	for _, test := range []struct {
		name       string
		kind       C.AdapterType
		directPool bool
		follow     bool
		want       string
	}{
		{"direct-without-pool", C.Direct, false, false, "203.0.113.53"},
		{"direct-without-pool-follow", C.Direct, false, true, "203.0.113.53"},
		{"direct-does-not-follow", C.Direct, true, false, "198.51.100.53"},
		{"direct-follows", C.Direct, true, true, "203.0.113.53"},
		{"compatible-follows", C.Compatible, true, true, "203.0.113.53"},
		{"proxy-uses-policy", C.Socks5, true, false, "203.0.113.53"},
	} {
		for _, qtype := range []uint16{D.TypeA, D.TypeTXT} {
			t.Run(fmt.Sprintf("%s/%s", test.name, D.TypeToString[qtype]), func(t *testing.T) {
				leaf := newRoutingTestOutbound("business")
				leaf.kind.Store(int32(test.kind))
				config := policyRoutingTestConfig()
				config.DirectFollowPolicy = test.follow
				if !test.directPool {
					config.DirectServer = nil
				}
				rs := NewResolver(config)
				t.Cleanup(rs.Close)
				query := new(D.Msg).SetQuestion("service.private.example.", qtype)
				ctx := routingTestContext(leaf, routingTestOrigin())
				routingTestExchange(t, rs.Resolver, ctx, query)
				nativeRoutingTestDestination(t, leaf, test.want)
				if leaf.queryCount.Load() != 1 {
					t.Fatal("policy/direct pool queried an unrelated main or fallback server")
				}
				prepared, _, err := rs.Resolver.prepareDNSRouting(ctx, query)
				if err != nil || queryRoute(prepared).plan.Type() != test.kind {
					t.Fatalf("nameserver policy changed the business exit: %v", err)
				}
			})
		}
	}
}

func TestDNSRuleRoutingPolicyCapabilitiesIgnoreUnusedExplicitServers(t *testing.T) {
	routingTestEnable(t)
	business, explicit := newRoutingTestOutbound("reject"), newRoutingTestOutbound("unused-explicit")
	business.kind.Store(int32(C.Reject))
	config := policyRoutingTestConfig()
	config.Main[0].ProxyAdapter = explicit
	config.Fallback[0].ProxyAdapter = explicit
	config.DirectServer[0].ProxyAdapter = explicit
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	answer := routingTestExchange(t, rs.Resolver, routingTestContext(business, routingTestOrigin()), routingTestQuery("blocked.private.example", 1))
	if answer.Rcode != D.RcodeRefused || explicit.queryCount.Load() != 0 || business.queryCount.Load() != 0 {
		t.Fatal("unused explicit servers weakened an automatic policy's business REJECT")
	}
}

func TestDNSRuleRoutingExplicitPolicyAndDirectFollowGate(t *testing.T) {
	routingTestEnable(t)
	for _, follow := range []bool{false, true} {
		t.Run(fmt.Sprintf("follow=%v", follow), func(t *testing.T) {
			business, policy := newRoutingTestOutbound("direct"), newRoutingTestOutbound("explicit-policy")
			config := policyRoutingTestConfig()
			config.DirectFollowPolicy = follow
			config.Policy[0].NameServers[0].ProxyAdapter = policy
			config.SpeedCheck = SpeedCheckConfig{Mode: []string{"tcp:80"}, Timeout: time.Second}
			rs := NewResolver(config)
			t.Cleanup(rs.Close)
			query := routingTestQuery("service.private.example", 2)
			ctx, reply, err := rs.Resolver.prepareDNSRouting(routingTestContext(business, routingTestOrigin()), query)
			if err != nil || reply != nil {
				t.Fatalf("prepare effective pool: reply=%v err=%v", reply, err)
			}
			main, fallback := rs.Resolver.dnsQueryServers(ctx, query)
			if rs.Resolver.directDNSQueryEligible(ctx, query) != !follow || rs.Resolver.speedCheckEligible(ctx, main, query) != !follow || len(fallback) != 0 {
				t.Fatal("prefetch/probe eligibility did not use the effective direct/policy pool")
			}
			// The speed checker was only needed to test admission. Keep this
			// exchange focused on its DNS destination and explicit exit.
			rs.Resolver.speedChecker = nil
			if _, err := rs.Resolver.ipExchange(ctx, query); err != nil {
				t.Fatal(err)
			}
			if follow {
				nativeRoutingTestDestination(t, policy, "203.0.113.53")
				if business.udp.Load() != 0 {
					t.Fatal("explicit policy lost its configured exit")
				}
			} else {
				nativeRoutingTestDestination(t, business, "198.51.100.53")
				if policy.udp.Load() != 0 {
					t.Fatal("direct-nameserver-follow-policy=false still contacted the policy server")
				}
			}
		})
	}
}

func TestDNSRuleRoutingPrivatePolicyFailureDoesNotBorrowPublicPools(t *testing.T) {
	routingTestEnable(t)
	business := &nativeRoutingTestFailure{newRoutingTestOutbound("failed-private")}
	business.kind.Store(int32(C.Socks5))
	config := policyRoutingTestConfig()
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	query := routingTestQuery("secret.private.example", 3)
	ctx, reply, err := rs.Resolver.prepareDNSRouting(routingTestContext(business, routingTestOrigin()), query)
	if err != nil || reply != nil {
		t.Fatalf("prepare private policy: reply=%v err=%v", reply, err)
	}
	if _, err := rs.Resolver.ipExchange(ctx, query); err == nil {
		t.Fatal("private DNS failure was silently replaced by a public answer")
	}
	for i := int32(0); i < business.udp.Load(); i++ {
		nativeRoutingTestDestination(t, business.routingTestOutbound, "203.0.113.53")
	}
	if business.udp.Load() != 2 {
		t.Fatal("private DNS exceeded its one same-exit reconnect or contacted another pool")
	}
}

func TestDNSRuleRoutingAllExplicitPoolsStillSelectByBusinessRoute(t *testing.T) {
	routingTestEnable(t)
	for _, follow := range []bool{false, true} {
		t.Run(fmt.Sprintf("follow=%v", follow), func(t *testing.T) {
			business, policy, direct := newRoutingTestOutbound("business"), newRoutingTestOutbound("policy"), newRoutingTestOutbound("direct-pool")
			unused := newRoutingTestOutbound("unused-main")
			config := policyRoutingTestConfig()
			config.DirectFollowPolicy = follow
			config.Main[0].ProxyAdapter = unused
			config.Fallback[0].ProxyAdapter = unused
			config.Policy[0].NameServers[0].ProxyAdapter = policy
			config.DirectServer[0].ProxyAdapter = direct
			rs := NewResolver(config)
			t.Cleanup(rs.Close)
			routingTestExchange(t, rs.Resolver, routingTestContext(business, routingTestOrigin()), routingTestQuery("explicit.private.example", 8))
			if follow {
				nativeRoutingTestDestination(t, policy, "203.0.113.53")
				if direct.queryCount.Load() != 0 {
					t.Fatal("follow-policy selected the original direct server")
				}
			} else {
				nativeRoutingTestDestination(t, direct, "198.51.100.53")
				if policy.queryCount.Load() != 0 {
					t.Fatal("explicit-only policy prevented business DIRECT pool selection")
				}
			}
			if business.queryCount.Load() != 0 || unused.queryCount.Load() != 0 {
				t.Fatal("business pool selection replaced the configured explicit DNS exit")
			}
		})
	}
}

func TestDNSUpstreamRoutingPolicyKeepsBusinessExitAndProbe(t *testing.T) {
	routingTestEnable(t)
	business, policy, unused := newRoutingTestOutbound("business-direct"), newRoutingTestOutbound("policy-dns"), newRoutingTestOutbound("unused")
	policy.kind.Store(int32(C.Socks5))
	upstreamRoutingTestRules(t, map[string]C.Proxy{
		"DIRECT": routingTestProxy(business), "policy": routingTestProxy(policy), "unused": routingTestProxy(unused),
	}, []C.Rule{
		upstreamRoutingTestIPRule(t, "203.0.113.53/32", "policy"),
		RC.NewMatch("unused"),
	})
	config := policyRoutingTestConfig()
	config.UpstreamRuleRouting = true
	config.DirectFollowPolicy = true
	config.SpeedCheck = SpeedCheckConfig{Mode: []string{"tcp:80"}, ResponseMode: "fastest-ip", Timeout: time.Second}
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	query := routingTestQuery("service.private.example", 4)
	ctx := upstreamRoutingTestContext(t, business)
	routingTestExchange(t, rs.Resolver, ctx, query)
	nativeRoutingTestDestination(t, policy, "203.0.113.53")
	probe := routingTestNextCall(t, business)
	if probe.metadata.DstIP.String() != "192.0.2.99" || probe.metadata.DstPort != 80 || business.tcp.Load() != 1 || business.udp.Load() != 0 || unused.udp.Load() != 0 || unused.tcp.Load() != 0 {
		t.Fatal("policy's independent DNS route changed business probing or used an unselected pool")
	}
}

type policyRoutingTestMatcher struct{ enabled atomic.Bool }

func (m *policyRoutingTestMatcher) MatchDomain(string) bool { return m.enabled.Load() }

func TestDNSRuleRoutingLiveExplicitPolicySeparatesCacheAndSingleflight(t *testing.T) {
	routingTestEnable(t)
	main, policy := newRoutingTestOutbound("main"), newRoutingTestOutbound("policy")
	main.answer = func(query *D.Msg) *D.Msg { return upstreamRoutingTestAnswerIP(query, "192.0.2.1") }
	policy.answer = func(query *D.Msg) *D.Msg { return upstreamRoutingTestAnswerIP(query, "192.0.2.2") }
	release := make(chan struct{})
	var once sync.Once
	unblock := func() { once.Do(func() { close(release) }) }
	defer unblock()
	policy.gate = release
	matcher := new(policyRoutingTestMatcher)
	matcher.enabled.Store(true)
	config := Config{
		RuleRouting: true,
		Main:        []NameServer{{Addr: "192.0.2.53:53", ProxyAdapter: main}},
		Policy:      []Policy{{Matcher: matcher, NameServers: []NameServer{{Addr: "203.0.113.53:53", ProxyAdapter: policy}}}},
		SpeedCheck:  SpeedCheckConfig{Mode: []string{}},
	}
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	query := routingTestQuery("live.private.example", 5)
	type result struct {
		answer *D.Msg
		err    error
	}
	first := make(chan result, 1)
	go func() {
		answer, err := rs.Resolver.ExchangeContext(ctx, query.Copy())
		first <- result{answer, err}
	}()
	select {
	case <-policy.questions:
	case <-ctx.Done():
		t.Fatal("private query did not start")
	}
	// Simulate a rule-provider refresh while its first answer is in flight.
	// An explicit-only pool has no business route to supply cache isolation.
	matcher.enabled.Store(false)
	answer := routingTestExchange(t, rs.Resolver, ctx, query.Copy())
	upstreamRoutingTestWantAnswer(t, answer, "192.0.2.1")
	unblock()
	select {
	case got := <-first:
		if got.err != nil {
			t.Fatal(got.err)
		}
		upstreamRoutingTestWantAnswer(t, got.answer, "192.0.2.2")
	case <-ctx.Done():
		t.Fatal("private query did not complete")
	}
	for _, enabled := range []bool{false, true} {
		matcher.enabled.Store(enabled)
		want := "192.0.2.1"
		if enabled {
			want = "192.0.2.2"
		}
		upstreamRoutingTestWantAnswer(t, routingTestExchange(t, rs.Resolver, ctx, query.Copy()), want)
	}
	if main.queryCount.Load() != 1 || policy.queryCount.Load() != 1 {
		t.Fatal("policy cache scopes did not reuse their own answers")
	}
}

func TestDNSRuleRoutingPrefetchStopsWhenPolicyPoolChanges(t *testing.T) {
	r, control, _ := cachePrefetchTestControl(t, &CacheOptions{Prefetch: true})
	clock := cachePrefetchTestClock(control)
	matcher := new(policyRoutingTestMatcher)
	matcher.enabled.Store(true)
	r.policy = []dnsPolicy{domainMatcherPolicy{matcher: matcher, dnsClients: transform([]NameServer{{Addr: "203.0.113.53:53"}}, nil)}}
	query := routingTestQuery("refresh.private.example", 6)
	ctx, key := cachePrefetchTestPrepare(t, control, query, routingTestOrigin())
	ctx = control.Observe(ctx, query, key)
	cachePrefetchTestStore(t, control, ctx, query, time.Minute)
	entry := cachePrefetchTestEntry(t, control, key)
	var calls atomic.Int32
	control.fetch = func(context.Context, *D.Msg) (*D.Msg, error) {
		calls.Add(1)
		return nil, nil
	}
	matcher.enabled.Store(false)
	clock.Store(entry.next.UnixNano())
	control.tick(control.now())
	cacheWaitIdle(t, control)
	control.mu.Lock()
	remaining := len(control.hot)
	control.mu.Unlock()
	if calls.Load() != 0 || remaining != 0 {
		t.Fatal("a private policy's old demand started prefetch against the replacement public pool")
	}
}

func TestDNSRuleRoutingPolicyPoolIsFrozenForEachQuery(t *testing.T) {
	routingTestEnable(t)
	business, upstream := newRoutingTestOutbound("business"), newRoutingTestOutbound("dns")
	upstreamRoutingTestRules(t, map[string]C.Proxy{"DIRECT": routingTestProxy(business), "dns": routingTestProxy(upstream)}, []C.Rule{RC.NewMatch("dns")})
	matcher := new(policyRoutingTestMatcher)
	matcher.enabled.Store(true)
	config := upstreamRoutingTestConfig()
	config.Policy = []Policy{{Matcher: matcher, NameServers: []NameServer{{Addr: "203.0.113.53:53"}}}}
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	query := routingTestQuery("frozen.private.example", 7)
	ctx, reply, err := rs.Resolver.prepareDNSRouting(icontext.WithDNSRoutingInbound(routingTestContext(business, routingTestOrigin())), query)
	if err != nil || reply != nil {
		t.Fatalf("prepare frozen policy: reply=%v err=%v", reply, err)
	}
	matcher.enabled.Store(false)
	ctx, err = rs.Resolver.prepareDNSUpstreamRouting(ctx, query)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := rs.Resolver.ipExchange(ctx, query); err != nil {
		t.Fatal(err)
	}
	nativeRoutingTestDestination(t, upstream, "203.0.113.53")
	if upstream.queryCount.Load() != 1 {
		t.Fatal("upstream planning and exchange selected different DNS pools")
	}
}
