package dns

import (
	"context"
	"encoding/base64"
	"errors"
	"io"
	"net"
	"net/netip"
	"sync"
	"testing"
	"time"

	"github.com/metacubex/http"
	"github.com/metacubex/http/httptest"
	"github.com/metacubex/mihomo/component/resolver"
	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"
	RC "github.com/metacubex/mihomo/rules/common"
	"github.com/metacubex/mihomo/tunnel"
	"github.com/metacubex/tls"

	D "github.com/miekg/dns"
)

func upstreamRoutingTestRules(t *testing.T, proxies map[string]C.Proxy, rules []C.Rule) {
	t.Helper()
	oldProxies, oldProviders := tunnel.Proxies(), tunnel.Providers()
	oldRules, oldRuleProviders := tunnel.Rules(), tunnel.RuleProviders()
	t.Cleanup(func() {
		tunnel.UpdateProxies(oldProxies, oldProviders)
		tunnel.UpdateRules(oldRules, nil, oldRuleProviders)
	})
	tunnel.UpdateProxies(proxies, nil)
	tunnel.UpdateRules(rules, nil, nil)
}

func upstreamRoutingTestIPRule(t *testing.T, prefix, outbound string) C.Rule {
	t.Helper()
	rule, err := RC.NewIPCIDR(prefix, outbound, RC.WithIPCIDRNoResolve(true))
	if err != nil {
		t.Fatal(err)
	}
	return rule
}

func upstreamRoutingTestConfig() Config {
	return Config{
		RuleRouting:         true,
		UpstreamRuleRouting: true,
		Main:                []NameServer{{Addr: "192.0.2.53:53"}},
		SpeedCheck:          SpeedCheckConfig{Mode: []string{}},
	}
}

func upstreamRoutingTestContext(t *testing.T, business C.ProxyAdapter) context.Context {
	t.Helper()
	ctx, cancel := context.WithTimeout(routingTestContext(business, routingTestOrigin()), 5*time.Second)
	t.Cleanup(cancel)
	return ctx
}

func upstreamRoutingTestAnswerIP(query *D.Msg, address string) *D.Msg {
	answer := routingTestAnswer(query, false)
	answer.Answer[0].(*D.A).A = net.ParseIP(address).To4()
	return answer
}

func upstreamRoutingTestWantAnswer(t *testing.T, answer *D.Msg, address string) {
	t.Helper()
	if len(answer.Answer) != 1 {
		t.Fatalf("unexpected DNS answer: %v", answer)
	}
	if rr, ok := answer.Answer[0].(*D.A); !ok || rr.A.String() != address {
		t.Fatalf("wrong DNS answer after upstream selection: %v, want %s", answer, address)
	}
}

func TestDNSUpstreamRoutingEachServerChoosesItsOwnExit(t *testing.T) {
	routingTestEnable(t)
	business := newRoutingTestOutbound("business-proxy")
	business.kind.Store(int32(C.Socks5))
	main, fallback := newRoutingTestOutbound("dns-main"), newRoutingTestOutbound("dns-fallback")
	upstreamRoutingTestRules(t, map[string]C.Proxy{
		"DIRECT": routingTestProxy(business),
		"main":   routingTestProxy(main),
		"backup": routingTestProxy(fallback),
	}, []C.Rule{
		upstreamRoutingTestIPRule(t, "192.0.2.53/32", "main"),
		upstreamRoutingTestIPRule(t, "198.51.100.53/32", "backup"),
		RC.NewMatch("DIRECT"),
	})
	config := upstreamRoutingTestConfig()
	config.Fallback = []NameServer{{Addr: "198.51.100.53:53"}}
	config.FallbackIPFilter = []C.IpMatcher{routingTestIPFilter{}}
	config.FallbackLazyQuery = true
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	answer := routingTestExchange(t, rs.Resolver, upstreamRoutingTestContext(t, business), routingTestQuery("website.example", 1))
	upstreamRoutingTestWantAnswer(t, answer, "192.0.2.99")
	nativeRoutingTestDestination(t, main, "192.0.2.53")
	nativeRoutingTestDestination(t, fallback, "198.51.100.53")
	if main.queryCount.Load() != 1 || fallback.queryCount.Load() != 1 || business.udp.Load() != 0 || business.tcp.Load() != 0 {
		t.Fatalf("DNS servers inherited a business/other server's exit: main=%d fallback=%d business UDP=%d TCP=%d",
			main.queryCount.Load(), fallback.queryCount.Load(), business.udp.Load(), business.tcp.Load())
	}
}

// This real HTTP/2 endpoint records the query after the configured ECS wrapper
// has run. Its response is then probed through the independent business DIRECT
// adapter, while the encrypted socket itself uses the DNS proxy adapter.
func upstreamRoutingTestDoH(t *testing.T) (NameServer, string, <-chan *D.Msg) {
	t.Helper()
	seen := make(chan *D.Msg, 8)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, request *http.Request) {
		var wire []byte
		var err error
		if request.Method == http.MethodPost {
			wire, err = io.ReadAll(request.Body)
		} else {
			wire, err = base64.RawURLEncoding.DecodeString(request.URL.Query().Get("dns"))
		}
		query := new(D.Msg)
		if err != nil || query.Unpack(wire) != nil || len(query.Question) != 1 || request.URL.Path != "/dns-query" {
			http.Error(w, "invalid DNS query", http.StatusBadRequest)
			return
		}
		select {
		case seen <- query.Copy():
		case <-request.Context().Done():
			return
		}
		wire, err = routingTestAnswer(query, false).Pack()
		if err != nil {
			http.Error(w, "could not encode DNS answer", http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "application/dns-message")
		_, _ = w.Write(wire)
	}))
	server.EnableHTTP2 = true
	server.TLS = &tls.Config{Certificates: []tls.Certificate{encryptedRoutingCertificate(t, true)}, MinVersion: tls.VersionTLS12}
	server.StartTLS()
	t.Cleanup(server.Close)
	return NameServer{Net: "https", Addr: server.URL + "/dns-query"}, server.Listener.Addr().String(), seen
}

func TestDNSUpstreamRoutingDirectPoolECSAndProbeSurviveProxiedDoH(t *testing.T) {
	routingTestEnable(t)
	directNS, endpoint, seen := upstreamRoutingTestDoH(t)
	directNS.Params = map[string]string{"ecs": "203.0.113.0/24", "ecs-override": "true"}
	business, unusedMain := newRoutingTestOutbound("website-direct"), newRoutingTestOutbound("unused-main")
	dnsProxy := newEncryptedRoutingOutbound("doh-proxy")
	upstreamRoutingTestRules(t, map[string]C.Proxy{
		"DIRECT": routingTestProxy(business),
		"dns":    routingTestProxy(dnsProxy),
		"unused": routingTestProxy(unusedMain),
	}, []C.Rule{
		RC.NewDomain("domestic.example", "DIRECT"),
		upstreamRoutingTestIPRule(t, "127.0.0.1/32", "dns"),
		RC.NewMatch("unused"),
	})
	config := upstreamRoutingTestConfig()
	config.DirectServer = []NameServer{directNS}
	config.SpeedCheck = SpeedCheckConfig{Mode: []string{"tcp:80"}, ResponseMode: "fastest-ip", Timeout: time.Second}
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	ctx, cancel := context.WithTimeout(icontext.WithDNSRoutingInbound(icontext.WithDNSRoutingMetadata(context.Background(), routingTestOrigin())), 5*time.Second)
	defer cancel()
	answer := routingTestExchange(t, rs.Resolver, ctx, routingTestQuery("domestic.example", 2))
	upstreamRoutingTestWantAnswer(t, answer, "192.0.2.99")
	var query *D.Msg
	select {
	case query = <-seen:
	case <-ctx.Done():
		t.Fatal("selected direct-nameserver did not receive the business query")
	}
	var subnet *D.EDNS0_SUBNET
	if opt := query.IsEdns0(); opt != nil {
		for _, option := range opt.Option {
			if value, ok := option.(*D.EDNS0_SUBNET); ok {
				subnet = value
			}
		}
	}
	if query.Question[0].Name != "domestic.example." || subnet == nil || subnet.Address.String() != "203.0.113.0" || subnet.SourceNetmask != 24 {
		t.Fatalf("DIRECT pool/ECS was replaced by the DNS transport's proxy classification: %v", query)
	}
	dnsProxy.assertDestination(t, endpoint)
	if unusedMain.udp.Load() != 0 || unusedMain.tcp.Load() != 0 || business.udp.Load() != 0 || business.tcp.Load() != 1 {
		t.Fatalf("business DIRECT lost its probe or carried the DoH socket: unused=%d/%d direct=%d/%d",
			unusedMain.udp.Load(), unusedMain.tcp.Load(), business.udp.Load(), business.tcp.Load())
	}
	probe := routingTestNextCall(t, business)
	if probe.metadata.DstIP.String() != "192.0.2.99" || probe.metadata.DstPort != 80 {
		t.Fatalf("DIRECT probe targeted the resolver instead of its returned website IP: %+v", probe.metadata)
	}
}

func TestDNSUpstreamRoutingProxiedBusinessWithDirectDNSDoesNotProbe(t *testing.T) {
	routingTestEnable(t)
	business, dnsDirect := newRoutingTestOutbound("website-proxy"), newRoutingTestOutbound("dns-direct")
	business.kind.Store(int32(C.Socks5))
	upstreamRoutingTestRules(t, map[string]C.Proxy{
		"DIRECT": routingTestProxy(dnsDirect),
	}, []C.Rule{RC.NewMatch("DIRECT")})
	config := upstreamRoutingTestConfig()
	config.SpeedCheck = SpeedCheckConfig{Mode: []string{"tcp:80"}, ResponseMode: "fastest-ip", Timeout: time.Second}
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	answer := routingTestExchange(t, rs.Resolver, upstreamRoutingTestContext(t, business), routingTestQuery("overseas.example", 3))
	upstreamRoutingTestWantAnswer(t, answer, "192.0.2.99")
	nativeRoutingTestDestination(t, dnsDirect, "192.0.2.53")
	if dnsDirect.queryCount.Load() != 1 || dnsDirect.tcp.Load() != 0 || business.udp.Load() != 0 || business.tcp.Load() != 0 {
		t.Fatal("DIRECT DNS transport incorrectly enabled destination probes for proxied business traffic")
	}
}

func TestDNSUpstreamRoutingStandaloneMatchesResolverAddressRules(t *testing.T) {
	for _, name := range []string{"ipcidr", "geoip-lan", "domain", "destination-port"} {
		t.Run(name, func(t *testing.T) {
			routingTestEnable(t)
			selected, unmatched, bootstrap := newRoutingTestOutbound("selected"), newRoutingTestOutbound("unmatched"), newRoutingTestOutbound("bootstrap")
			selected.kind.Store(int32(C.Socks5))
			config := upstreamRoutingTestConfig()
			config.RuleRouting = false
			var rule C.Rule
			var err error
			switch name {
			case "ipcidr":
				rule = upstreamRoutingTestIPRule(t, "192.0.2.53/32", "selected")
			case "geoip-lan":
				config.Main[0].Addr = "10.23.0.53:53"
				rule, err = RC.NewGEOIP("lan", "selected", false, true)
			case "domain":
				config.Main[0].Addr = "resolver.example:53"
				config.Default = []NameServer{{Addr: "192.0.2.1:53", ProxyAdapter: bootstrap}}
				bootstrap.answer = func(query *D.Msg) *D.Msg { return upstreamRoutingTestAnswerIP(query, "192.0.2.53") }
				rule = RC.NewDomain("resolver.example", "selected")
			case "destination-port":
				config.Main[0].Addr = "192.0.2.53:5353"
				rule, err = RC.NewPort("5353", "selected", C.DstPort)
			}
			if err != nil {
				t.Fatal(err)
			}
			upstreamRoutingTestRules(t, map[string]C.Proxy{
				"DIRECT":    routingTestProxy(unmatched),
				"unmatched": routingTestProxy(unmatched),
				"selected":  routingTestProxy(selected),
			}, []C.Rule{rule, RC.NewMatch("unmatched")})
			rs := NewResolver(config)
			t.Cleanup(rs.Close)
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			routingTestExchange(t, rs.Resolver, ctx, routingTestQuery("business.example", 4))
			call := routingTestNextCall(t, selected)
			want := "192.0.2.53:53"
			if name == "geoip-lan" || name == "destination-port" {
				want = config.Main[0].Addr
			}
			if call.metadata.AddrPort() != netip.MustParseAddrPort(want) || call.metadata.Host != "" || unmatched.queryCount.Load() != 0 {
				t.Fatalf("upstream rules saw a business name/port instead of the resolver: %+v; unmatched=%d", call.metadata, unmatched.queryCount.Load())
			}
			if name == "domain" && bootstrap.queryCount.Load() == 0 {
				t.Fatal("resolver hostname was not resolved through its independent bootstrap")
			}
		})
	}
}

func TestDNSUpstreamRoutingExplicitExitWins(t *testing.T) {
	for _, name := range []string{"named-exit", "DIRECT", "adapter"} {
		t.Run(name, func(t *testing.T) {
			routingTestEnable(t)
			business, explicit, automatic := newRoutingTestOutbound("business"), newRoutingTestOutbound("explicit"), newRoutingTestOutbound("automatic")
			business.kind.Store(int32(C.Socks5))
			upstreamRoutingTestRules(t, map[string]C.Proxy{
				"DIRECT":    routingTestProxy(explicit),
				"fixed":     routingTestProxy(explicit),
				"automatic": routingTestProxy(automatic),
			}, []C.Rule{RC.NewMatch("automatic")})
			config := upstreamRoutingTestConfig()
			// Keep an automatic but unselected direct pool so this also covers
			// an explicit transport inside an otherwise live business plan.
			config.DirectServer = []NameServer{{Addr: "198.51.100.53:53"}}
			switch name {
			case "named-exit":
				config.Main[0].ProxyName = "fixed"
			case "DIRECT":
				config.Main[0].ProxyName = "DIRECT"
			case "adapter":
				config.Main[0].ProxyAdapter = explicit
			}
			rs := NewResolver(config)
			t.Cleanup(rs.Close)
			routingTestExchange(t, rs.Resolver, upstreamRoutingTestContext(t, business), routingTestQuery("business.example", 5))
			nativeRoutingTestDestination(t, explicit, "192.0.2.53")
			if explicit.queryCount.Load() != 1 || automatic.queryCount.Load() != 0 || business.queryCount.Load() != 0 {
				t.Fatal("automatic resolver rules overrode an explicitly configured DNS exit")
			}
		})
	}
}

func TestDNSUpstreamRoutingBusinessRejectStillPrecedesCache(t *testing.T) {
	routingTestEnable(t)
	business, carrier := newRoutingTestOutbound("business"), newRoutingTestOutbound("dns-carrier")
	business.kind.Store(int32(C.Socks5))
	upstreamRoutingTestRules(t, map[string]C.Proxy{"DIRECT": routingTestProxy(carrier)}, []C.Rule{RC.NewMatch("DIRECT")})
	rs := NewResolver(upstreamRoutingTestConfig())
	t.Cleanup(rs.Close)
	ctx, query := upstreamRoutingTestContext(t, business), routingTestQuery("blocked.example", 6)
	routingTestExchange(t, rs.Resolver, ctx, query)
	business.kind.Store(int32(C.Reject))
	answer := routingTestExchange(t, rs.Resolver, ctx, query)
	if answer.Rcode != D.RcodeRefused || len(answer.Answer) != 0 {
		t.Fatalf("independent DNS transport/cached answer bypassed the business REJECT: %v", answer)
	}
	business.kind.Store(int32(C.RejectDrop))
	if _, err := rs.Resolver.ExchangeContext(ctx, query); !errors.Is(err, resolver.ErrDNSDrop) {
		t.Fatalf("business REJECT-DROP lost its silent behavior: %v", err)
	}
	if carrier.queryCount.Load() != 1 || business.queryCount.Load() != 0 {
		t.Fatal("rejected business question contacted an upstream resolver")
	}
}

func TestDNSUpstreamRoutingDisabledPreservesBusinessExit(t *testing.T) {
	routingTestEnable(t)
	business, upstream := newRoutingTestOutbound("business"), newRoutingTestOutbound("upstream")
	business.kind.Store(int32(C.Socks5))
	upstreamRoutingTestRules(t, map[string]C.Proxy{"DIRECT": routingTestProxy(upstream)}, []C.Rule{RC.NewMatch("DIRECT")})
	config := upstreamRoutingTestConfig()
	config.UpstreamRuleRouting = false
	config.Main[0].ProxyName = RespectRules
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	routingTestExchange(t, rs.Resolver, upstreamRoutingTestContext(t, business), routingTestQuery("legacy.example", 7))
	nativeRoutingTestDestination(t, business, "192.0.2.53")
	if business.queryCount.Load() != 1 || upstream.queryCount.Load() != 0 {
		t.Fatal("default-disabled feature changed existing dns-rule-routing/#RULES behavior")
	}
}

func TestDNSUpstreamRoutingNonRuleModesPreserveLegacyRespectRules(t *testing.T) {
	for _, mode := range []tunnel.TunnelMode{tunnel.Global, tunnel.Direct} {
		t.Run(mode.String(), func(t *testing.T) {
			routingTestEnable(t)
			legacy, ruleExit := newRoutingTestOutbound("mode-exit"), newRoutingTestOutbound("rule-exit")
			upstreamRoutingTestRules(t, map[string]C.Proxy{
				"DIRECT": routingTestProxy(legacy),
				"GLOBAL": routingTestProxy(legacy),
				"rule":   routingTestProxy(ruleExit),
			}, []C.Rule{RC.NewMatch("rule")})
			tunnel.SetMode(mode)
			config := upstreamRoutingTestConfig()
			config.Main[0].ProxyName = RespectRules
			rs := NewResolver(config)
			t.Cleanup(rs.Close)
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			routingTestExchange(t, rs.Resolver, ctx, routingTestQuery("mode.example", 8))
			if legacy.queryCount.Load() != 1 || ruleExit.queryCount.Load() != 0 {
				t.Fatal("independent upstream routing unexpectedly bypassed Global/Direct mode")
			}
		})
	}
}

func TestDNSUpstreamRoutingGroupSwitchSeparatesAnswerAndCandidateCaches(t *testing.T) {
	routingTestEnable(t)
	business, first, second := newRoutingTestOutbound("business-direct"), newRoutingTestOutbound("first-dns-exit"), newRoutingTestOutbound("second-dns-exit")
	first.kind.Store(int32(C.Socks5))
	second.kind.Store(int32(C.Socks5))
	first.answer = func(query *D.Msg) *D.Msg { return upstreamRoutingTestAnswerIP(query, "192.0.2.10") }
	second.answer = func(query *D.Msg) *D.Msg { return upstreamRoutingTestAnswerIP(query, "198.51.100.10") }
	firstProxy, secondProxy := routingTestProxy(first), routingTestProxy(second)
	group := &routingTestGroup{routingTestBase: &routingTestBase{name: "dns-group", kind: C.Selector}, leaf: firstProxy}
	upstreamRoutingTestRules(t, map[string]C.Proxy{
		"DIRECT": routingTestProxy(business),
		"dns":    routingTestProxy(group),
	}, []C.Rule{RC.NewMatch("dns")})
	config := upstreamRoutingTestConfig()
	config.SpeedCheck = SpeedCheckConfig{Mode: []string{"tcp:80"}, ResponseMode: "fastest-ip", Timeout: time.Second}
	rs := NewResolver(config)
	t.Cleanup(rs.Close)
	ctx, query := upstreamRoutingTestContext(t, business), routingTestQuery("same.example", 9)
	upstreamRoutingTestWantAnswer(t, routingTestExchange(t, rs.Resolver, ctx, query), "192.0.2.10")
	query.Id++
	upstreamRoutingTestWantAnswer(t, routingTestExchange(t, rs.Resolver, ctx, query), "192.0.2.10")
	if first.queryCount.Load() != 1 {
		t.Fatal("unchanged DNS exit stopped reusing its answer cache")
	}
	group.leaf = secondProxy
	query.Id++
	upstreamRoutingTestWantAnswer(t, routingTestExchange(t, rs.Resolver, ctx, query), "198.51.100.10")
	if first.queryCount.Load() != 1 || second.queryCount.Load() != 1 {
		t.Fatal("switching only the DNS exit reused an old answer/candidate or queried the old exit")
	}
	group.leaf = firstProxy
	query.Id++
	upstreamRoutingTestWantAnswer(t, routingTestExchange(t, rs.Resolver, ctx, query), "192.0.2.10")
	if first.queryCount.Load() != 1 || second.queryCount.Load() != 1 || business.tcp.Load() != 2 || business.udp.Load() != 0 {
		t.Fatalf("separate DNS exits lost scoped caches or business DIRECT probe identity: queries=%d/%d probes=%d business DNS=%d",
			first.queryCount.Load(), second.queryCount.Load(), business.tcp.Load(), business.udp.Load())
	}
}

func TestDNSUpstreamRoutingEncryptedProtocolsUseIndependentReusableExit(t *testing.T) {
	for _, protocol := range []string{"h2", "h3", "tls", "quic"} {
		t.Run(protocol, func(t *testing.T) {
			routingTestEnable(t)
			server := newEncryptedRoutingServer(t, protocol, true, nil)
			business := newRoutingTestOutbound("business")
			business.kind.Store(int32(C.Socks5))
			carrier := newEncryptedRoutingOutbound("dns-exit")
			upstreamRoutingTestRules(t, map[string]C.Proxy{
				"DIRECT": routingTestProxy(business),
				"dns":    routingTestProxy(carrier),
			}, []C.Rule{
				upstreamRoutingTestIPRule(t, "127.0.0.1/32", "dns"),
				RC.NewMatch("DIRECT"),
			})
			config := upstreamRoutingTestConfig()
			config.Main = []NameServer{server.ns}
			rs := NewResolver(config)
			t.Cleanup(rs.Close)
			ctx := upstreamRoutingTestContext(t, business)
			routingTestExchange(t, rs.Resolver, ctx, routingTestQuery("first.example", 10))
			first := server.next(t)
			dials := carrier.count()
			routingTestExchange(t, rs.Resolver, ctx, routingTestQuery("second.example", 11))
			second := server.next(t)
			if dials == 0 || carrier.count() != dials || first.connection != second.connection || first.protocol != second.protocol || business.tcp.Load() != 0 || business.udp.Load() != 0 {
				t.Fatalf("encrypted DNS inherited the business exit or lost connection reuse: first=%+v second=%+v dials=%d/%d", first, second, dials, carrier.count())
			}
			carrier.assertDestination(t, server.address)
		})
	}
}

func upstreamRoutingTestBlock(outbound *routingTestOutbound) func() {
	gate := make(chan struct{})
	var once sync.Once
	outbound.gate = gate
	return func() { once.Do(func() { close(gate) }) }
}

func upstreamRoutingTestWaitQuestion(t *testing.T, ctx context.Context, outbound *routingTestOutbound, bootstrap *routingTestOutbound) {
	t.Helper()
	select {
	case <-outbound.questions:
	case <-ctx.Done():
		t.Fatalf("healthy IP DNS was not dispatched before the caller deadline: healthy=%d bootstrap=%d err=%v",
			outbound.queryCount.Load(), bootstrap.queryCount.Load(), ctx.Err())
	}
}

func TestDNSUpstreamRoutingHungBootstrapDoesNotBlockHealthyIP(t *testing.T) {
	routingTestEnable(t)
	business, healthy, bootstrap := newRoutingTestOutbound("business"), newRoutingTestOutbound("healthy-ip"), newRoutingTestOutbound("hung-bootstrap")
	business.kind.Store(int32(C.Socks5))
	releaseHealthy, releaseBootstrap := upstreamRoutingTestBlock(healthy), upstreamRoutingTestBlock(bootstrap)
	upstreamRoutingTestRules(t, map[string]C.Proxy{"DIRECT": routingTestProxy(healthy)}, []C.Rule{RC.NewMatch("DIRECT")})
	config := upstreamRoutingTestConfig()
	config.Main = append(config.Main, NameServer{Net: "https", Addr: "https://blocked-resolver.example:443/dns-query"})
	config.Default = []NameServer{{Addr: "198.51.100.1:53", ProxyAdapter: bootstrap}}
	rs := NewResolver(config)
	t.Cleanup(func() { releaseHealthy(); releaseBootstrap(); rs.Close() })
	ctx, cancel := context.WithTimeout(routingTestContext(business, routingTestOrigin()), 500*time.Millisecond)
	defer cancel()
	result := sharedDNSAsync(rs.Resolver, ctx, routingTestQuery("healthy.example", 20))
	// The healthy response cannot arrive until its real wire query is seen.
	// Bootstrap is never released during the exchange, so success cannot be
	// explained by the unrelated resolver name eventually resolving.
	upstreamRoutingTestWaitQuestion(t, ctx, healthy, bootstrap)
	releaseHealthy()
	got := sharedDNSResult(t, result)
	if got.err != nil {
		t.Fatalf("a hanging sibling bootstrap blocked a healthy IP DNS reply: %v", got.err)
	}
	upstreamRoutingTestWantAnswer(t, got.message, "192.0.2.99")
}

func TestDNSUpstreamRoutingLazyFallbackDoesNotBootstrapUnusedServer(t *testing.T) {
	routingTestEnable(t)
	business, healthy, bootstrap := newRoutingTestOutbound("business"), newRoutingTestOutbound("main-ip"), newRoutingTestOutbound("unused-bootstrap")
	business.kind.Store(int32(C.Socks5))
	releaseHealthy, releaseBootstrap := upstreamRoutingTestBlock(healthy), upstreamRoutingTestBlock(bootstrap)
	upstreamRoutingTestRules(t, map[string]C.Proxy{"DIRECT": routingTestProxy(healthy)}, []C.Rule{RC.NewMatch("DIRECT")})
	config := upstreamRoutingTestConfig()
	config.Fallback = []NameServer{{Net: "https", Addr: "https://unused-fallback.example:443/dns-query"}}
	config.FallbackLazyQuery = true
	config.Default = []NameServer{{Addr: "198.51.100.1:53", ProxyAdapter: bootstrap}}
	rs := NewResolver(config)
	t.Cleanup(func() { releaseHealthy(); releaseBootstrap(); rs.Close() })
	ctx, cancel := context.WithTimeout(routingTestContext(business, routingTestOrigin()), 500*time.Millisecond)
	defer cancel()
	result := sharedDNSAsync(rs.Resolver, ctx, routingTestQuery("main-success.example", 21))
	upstreamRoutingTestWaitQuestion(t, ctx, healthy, bootstrap)
	if bootstrap.queryCount.Load() != 0 {
		t.Fatal("lazy fallback bootstrap started before main had even answered")
	}
	releaseHealthy()
	got := sharedDNSResult(t, result)
	if got.err != nil {
		t.Fatalf("unused fallback bootstrap delayed an accepted main reply: %v", got.err)
	}
	upstreamRoutingTestWantAnswer(t, got.message, "192.0.2.99")
	if bootstrap.queryCount.Load() != 0 {
		t.Fatal("successful main lookup contacted the unused lazy fallback bootstrap")
	}
}

func TestDNSUpstreamRoutingForcedSOADoesNotRequireBootstrap(t *testing.T) {
	routingTestEnable(t)
	business, carrier, bootstrap := newRoutingTestOutbound("business"), newRoutingTestOutbound("unused-dns"), newRoutingTestOutbound("hung-bootstrap")
	business.kind.Store(int32(C.Socks5))
	releaseBootstrap := upstreamRoutingTestBlock(bootstrap)
	upstreamRoutingTestRules(t, map[string]C.Proxy{"DIRECT": routingTestProxy(carrier)}, []C.Rule{RC.NewMatch("DIRECT")})
	config := upstreamRoutingTestConfig()
	config.Main = []NameServer{{Net: "https", Addr: "https://blocked-resolver.example:443/dns-query"}}
	config.Default = []NameServer{{Addr: "198.51.100.1:53", ProxyAdapter: bootstrap}}
	config.AnswerPolicy.ForceAAAASOA = true
	rs := NewResolver(config)
	t.Cleanup(func() { releaseBootstrap(); rs.Close() })
	ctx, cancel := context.WithTimeout(routingTestContext(business, routingTestOrigin()), 500*time.Millisecond)
	defer cancel()
	query := new(D.Msg).SetQuestion("local-policy.example.", D.TypeAAAA)
	answer, err := rs.Resolver.ExchangeContext(ctx, query)
	if err != nil {
		t.Fatalf("local forced SOA depended on upstream bootstrap: err=%v bootstrap queries=%d", err, bootstrap.queryCount.Load())
	}
	assertAnswerPolicySOA(t, query, answer, 30)
	if bootstrap.queryCount.Load() != 0 || carrier.queryCount.Load() != 0 || carrier.tcp.Load() != 0 {
		t.Fatal("local forced SOA contacted an upstream or its bootstrap resolver")
	}
}

func TestDNSUpstreamRoutingMixedExplicitRejectDoesNotContactAutomaticServer(t *testing.T) {
	for _, kind := range []C.AdapterType{C.Reject, C.RejectDrop} {
		t.Run(kind.String(), func(t *testing.T) {
			routingTestEnable(t)
			business, explicit, automatic, bootstrap := newRoutingTestOutbound("business"), newRoutingTestOutbound("explicit"), newRoutingTestOutbound("automatic"), newRoutingTestOutbound("bootstrap")
			business.kind.Store(int32(kind))
			upstreamRoutingTestRules(t, map[string]C.Proxy{"DIRECT": routingTestProxy(automatic)}, []C.Rule{RC.NewMatch("DIRECT")})
			config := upstreamRoutingTestConfig()
			config.Main = []NameServer{
				{Net: "https", Addr: "https://automatic-resolver.example:443/dns-query"},
				{Addr: "198.51.100.53:53", ProxyAdapter: explicit},
			}
			config.Default = []NameServer{{Addr: "198.51.100.1:53", ProxyAdapter: bootstrap}}
			rs := NewResolver(config)
			t.Cleanup(rs.Close)
			answer := routingTestExchange(t, rs.Resolver, upstreamRoutingTestContext(t, business), routingTestQuery("blocked.example", 22))
			upstreamRoutingTestWantAnswer(t, answer, "192.0.2.99")
			if explicit.queryCount.Load() != 1 || automatic.queryCount.Load() != 0 || automatic.tcp.Load() != 0 || bootstrap.queryCount.Load() != 0 || business.queryCount.Load() != 0 {
				t.Fatalf("explicit exception allowed rejected automatic DNS work: explicit=%d automatic=%d/%d bootstrap=%d business=%d",
					explicit.queryCount.Load(), automatic.queryCount.Load(), automatic.tcp.Load(), bootstrap.queryCount.Load(), business.queryCount.Load())
			}
		})
	}
}

func TestDNSUpstreamRoutingHostnameQUICUsesPinnedBootstrapDestination(t *testing.T) {
	for _, protocol := range []string{"h3", "quic"} {
		t.Run(protocol, func(t *testing.T) {
			routingTestEnable(t)
			server := newEncryptedRoutingServer(t, protocol, true, nil)
			_, port, err := net.SplitHostPort(server.address)
			if err != nil {
				t.Fatal(err)
			}
			ns := server.ns
			if ns.Params == nil {
				ns.Params = make(map[string]string)
			}
			// Keep hostname-based routing and an actual bootstrap lookup while
			// trusting only the fixture's localhost certificate.
			ns.Params["name-cert-verify"] = "localhost"
			ns.Addr = net.JoinHostPort("dns-bootstrap.example", port)
			if protocol == "h3" {
				ns.Addr = "https://" + ns.Addr + "/dns-query"
			}
			business, bootstrap := newRoutingTestOutbound("business"), newRoutingTestOutbound("bootstrap")
			business.kind.Store(int32(C.Socks5))
			bootstrap.answer = func(query *D.Msg) *D.Msg { return upstreamRoutingTestAnswerIP(query, "127.0.0.1") }
			carrier := newEncryptedRoutingOutbound("dns-exit")
			upstreamRoutingTestRules(t, map[string]C.Proxy{
				"DIRECT": routingTestProxy(business),
				"dns":    routingTestProxy(carrier),
			}, []C.Rule{RC.NewDomain("dns-bootstrap.example", "dns"), RC.NewMatch("DIRECT")})
			config := upstreamRoutingTestConfig()
			config.Main = []NameServer{ns}
			config.Default = []NameServer{{Addr: "192.0.2.1:53", ProxyAdapter: bootstrap}}
			rs := NewResolver(config)
			t.Cleanup(rs.Close)
			ctx := upstreamRoutingTestContext(t, business)
			routingTestExchange(t, rs.Resolver, ctx, routingTestQuery("first-hostname.example", 23))
			first := server.next(t)
			dials := carrier.count()
			routingTestExchange(t, rs.Resolver, ctx, routingTestQuery("second-hostname.example", 24))
			second := server.next(t)
			if bootstrap.queryCount.Load() == 0 || dials == 0 || carrier.count() != dials || first.connection != second.connection || business.queryCount.Load() != 0 {
				t.Fatalf("hostname QUIC changed its pinned destination or lost reuse: bootstrap=%d dials=%d/%d first=%+v second=%+v",
					bootstrap.queryCount.Load(), dials, carrier.count(), first, second)
			}
			carrier.assertDestination(t, server.address)
		})
	}
}

type upstreamRoutingTestIPFilterFunc func(netip.Addr) bool

func (f upstreamRoutingTestIPFilterFunc) MatchIp(ip netip.Addr) bool { return f(ip) }

func TestDNSUpstreamRoutingCancelledMainBootstrapDoesNotPoisonLazyFallback(t *testing.T) {
	routingTestEnable(t)
	business, healthy, bootstrap, fallback := newRoutingTestOutbound("business"), newRoutingTestOutbound("fast-main"), newRoutingTestOutbound("bootstrap"), newRoutingTestOutbound("fallback-exit")
	business.kind.Store(int32(C.Socks5))
	fallback.kind.Store(int32(C.Socks5))
	bootstrap.answer = func(query *D.Msg) *D.Msg { return upstreamRoutingTestAnswerIP(query, "192.0.2.55") }
	fallback.answer = func(query *D.Msg) *D.Msg {
		if opt := query.IsEdns0(); opt != nil {
			for _, option := range opt.Option {
				if subnet, ok := option.(*D.EDNS0_SUBNET); ok && subnet.Address.String() == "198.51.100.0" {
					return upstreamRoutingTestAnswerIP(query, "198.51.100.44")
				}
			}
		}
		return upstreamRoutingTestAnswerIP(query, "192.0.2.111")
	}
	releaseHealthy, releaseBootstrap := upstreamRoutingTestBlock(healthy), upstreamRoutingTestBlock(bootstrap)
	upstreamRoutingTestRules(t, map[string]C.Proxy{
		"DIRECT": routingTestProxy(business),
		"main":   routingTestProxy(healthy),
		"dns":    routingTestProxy(fallback),
	}, []C.Rule{
		upstreamRoutingTestIPRule(t, "192.0.2.53/32", "main"),
		RC.NewDomain("same-resolver.example", "dns"),
		RC.NewMatch("DIRECT"),
	})
	config := upstreamRoutingTestConfig()
	// Different ECS wrappers deliberately share one raw hostname transport.
	// Cancelling its unused main exchange must not cancel a later fallback
	// exchange that has its own live context and query parameters.
	config.Main = append(config.Main, NameServer{Addr: "same-resolver.example:53", Params: map[string]string{"ecs": "203.0.113.0/24"}})
	config.Fallback = []NameServer{{Addr: "same-resolver.example:53", Params: map[string]string{"ecs": "198.51.100.0/24"}}}
	config.FallbackLazyQuery = true
	config.FallbackIPFilter = []C.IpMatcher{upstreamRoutingTestIPFilterFunc(func(netip.Addr) bool {
		// Main's picker has joined its cancelled sibling before evaluating
		// this filter. Bootstrap becomes answerable only for the fallback.
		releaseBootstrap()
		return true
	})}
	config.Default = []NameServer{{Addr: "198.51.100.1:53", ProxyAdapter: bootstrap}}
	rs := NewResolver(config)
	t.Cleanup(func() { releaseHealthy(); releaseBootstrap(); rs.Close() })
	ctx, cancel := context.WithTimeout(routingTestContext(business, routingTestOrigin()), time.Second)
	defer cancel()
	result := sharedDNSAsync(rs.Resolver, ctx, routingTestQuery("fallback-required.example", 25))
	select {
	case <-bootstrap.questions:
	case <-ctx.Done():
		t.Fatal("main hostname bootstrap did not start before the test deadline")
	}
	upstreamRoutingTestWaitQuestion(t, ctx, healthy, bootstrap)
	releaseHealthy()
	got := sharedDNSResult(t, result)
	if got.err != nil {
		t.Fatalf("cancellation of main hostname lookup poisoned the live lazy fallback: %v; fallback queries=%d", got.err, fallback.queryCount.Load())
	}
	upstreamRoutingTestWantAnswer(t, got.message, "198.51.100.44")
	if fallback.queryCount.Load() != 1 {
		t.Fatalf("the fallback ECS lookup did not use its own live exchange: queries=%d", fallback.queryCount.Load())
	}
}

type upstreamRoutingWaitContext struct {
	context.Context
	waiting chan struct{}
	once    sync.Once
}

func (c *upstreamRoutingWaitContext) Done() <-chan struct{} {
	// resolve checks Err before taking the lock. Observing Done establishes
	// that this caller reached the in-flight owner's cancellable wait path.
	c.once.Do(func() { close(c.waiting) })
	return c.Context.Done()
}

func TestDNSUpstreamRoutingPendingWaiterCancellationDoesNotWaitForBootstrap(t *testing.T) {
	routingTestEnable(t)
	bootstrap, carrier := newRoutingTestOutbound("blocked-bootstrap"), newRoutingTestOutbound("dns-exit")
	carrier.kind.Store(int32(C.Socks5))
	bootstrap.answer = func(query *D.Msg) *D.Msg { return upstreamRoutingTestAnswerIP(query, "192.0.2.53") }
	releaseBootstrap := upstreamRoutingTestBlock(bootstrap)
	upstreamRoutingTestRules(t, map[string]C.Proxy{"DIRECT": routingTestProxy(carrier)}, []C.Rule{RC.NewMatch("DIRECT")})
	rs := NewResolver(Config{Main: []NameServer{{Addr: "198.51.100.1:53", ProxyAdapter: bootstrap}}, SpeedCheck: SpeedCheckConfig{Mode: []string{}}})
	t.Cleanup(func() { releaseBootstrap(); rs.Close() })
	dialer := newDNSDialer(rs.Resolver, nil, "")
	pending := &dnsUpstreamPlan{pending: true, network: C.UDP, endpoint: "pending-resolver.example:53", qname: "business.example"}
	ownerCtx, cancelOwner := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancelOwner()
	type planResult struct {
		plan *tunnel.DNSRoutingPlan
		err  error
	}
	owner := make(chan planResult, 1)
	go func() {
		plan, err := pending.resolve(ownerCtx, dialer)
		owner <- planResult{plan, err}
	}()
	select {
	case <-bootstrap.questions:
	case <-ownerCtx.Done():
		t.Fatal("the owner did not enter its blocking bootstrap")
	}
	waiterBase, cancelWaiter := context.WithCancel(context.Background())
	defer cancelWaiter()
	waiterCtx := &upstreamRoutingWaitContext{Context: waiterBase, waiting: make(chan struct{})}
	waiter := make(chan error, 1)
	go func() {
		_, err := pending.resolve(waiterCtx, dialer)
		waiter <- err
	}()
	select {
	case <-waiterCtx.waiting:
	case <-ownerCtx.Done():
		t.Fatal("the second caller could not enter an independently cancellable wait")
	}
	cancelWaiter()
	select {
	case err := <-waiter:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("cancelled waiter returned the owner's result instead of its own cancellation: %v", err)
		}
	case <-time.After(500 * time.Millisecond):
		t.Fatal("cancelled waiter remained blocked behind another caller's bootstrap")
	}
	select {
	case got := <-owner:
		t.Fatalf("cancelling a waiter also ended the still-live owner: %+v", got)
	default:
	}
	releaseBootstrap()
	select {
	case got := <-owner:
		if got.err != nil || got.plan == nil || got.plan.Type() != C.Socks5 {
			t.Fatalf("the owner's live bootstrap was poisoned by a cancelled waiter: %+v", got)
		}
	case <-ownerCtx.Done():
		t.Fatal("live owner did not finish after its bootstrap was released")
	}
}
