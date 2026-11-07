package dns

import (
	"context"
	"errors"
	"net"
	"sync"
	"testing"
	"time"

	"github.com/metacubex/mihomo/component/resolver"
	C "github.com/metacubex/mihomo/constant"
	RC "github.com/metacubex/mihomo/rules/common"
	"github.com/metacubex/mihomo/tunnel"

	D "github.com/miekg/dns"
)

func TestDNSUpstreamRuleRoutingSeparatesNodeResolverBootstrap(t *testing.T) {
	routingTestEnable(t)
	oldProxies, oldProviders := tunnel.Proxies(), tunnel.Providers()
	oldRules, oldRuleProviders := tunnel.Rules(), tunnel.RuleProviders()
	oldDisableIPv6 := resolver.DisableIPv6
	resolver.DisableIPv6 = true
	t.Cleanup(func() {
		tunnel.UpdateProxies(oldProxies, oldProviders)
		tunnel.UpdateRules(oldRules, nil, oldRuleProviders)
		resolver.DisableIPv6 = oldDisableIPv6
	})
	for _, policy := range []bool{false, true} {
		name := "node-pool"
		if policy {
			name = "node-policy"
		}
		t.Run(name, func(t *testing.T) {
			var mu sync.Mutex
			directQuestions := make(map[string]int)
			packet, err := net.ListenPacket("udp", "127.0.0.1:0")
			if err != nil {
				t.Fatal(err)
			}
			started, done := make(chan struct{}), make(chan error, 1)
			server := &D.Server{
				PacketConn:        packet,
				NotifyStartedFunc: func() { close(started) },
				Handler: D.HandlerFunc(func(w D.ResponseWriter, query *D.Msg) {
					mu.Lock()
					directQuestions[query.Question[0].Name]++
					mu.Unlock()
					answer := routingTestAnswer(query, false)
					ip := net.IPv4(192, 0, 2, 77)
					if query.Question[0].Name == "resolver.bootstrap.test." {
						ip = net.IPv4(127, 0, 0, 1)
					}
					answer.Answer[0].(*D.A).A = ip
					_ = w.WriteMsg(answer)
				}),
			}
			go func() { done <- server.ActivateAndServe() }()
			t.Cleanup(func() {
				_ = server.Shutdown()
				select {
				case <-done:
				case <-time.After(time.Second):
					t.Error("bootstrap test DNS server did not stop")
				}
			})
			select {
			case <-started:
			case err := <-done:
				t.Fatalf("bootstrap test DNS server failed to start: %v", err)
			case <-time.After(time.Second):
				t.Fatal("bootstrap test DNS server did not start")
			}
			_, port, err := net.SplitHostPort(packet.LocalAddr().String())
			if err != nil {
				t.Fatal(err)
			}
			proxy := newRoutingTestOutbound("bootstrap-proxy")
			proxy.kind.Store(int32(C.Socks5))
			proxy.answer = func(query *D.Msg) *D.Msg {
				answer := routingTestAnswer(query, false)
				if query.Question[0].Name == "resolver.bootstrap.test." {
					answer.Answer[0].(*D.A).A = net.IPv4(127, 0, 0, 1)
				}
				return answer
			}
			tunnel.UpdateProxies(map[string]C.Proxy{"bootstrap-proxy": routingTestProxy(proxy)}, nil)
			tunnel.UpdateRules([]C.Rule{RC.NewMatch("bootstrap-proxy")}, nil, nil)
			shared := NameServer{Addr: net.JoinHostPort("resolver.bootstrap.test", port)}
			cfg := Config{
				UpstreamRuleRouting: true,
				Main:                []NameServer{shared},
				Default:             []NameServer{{Addr: packet.LocalAddr().String()}},
				ProxyServer:         []NameServer{shared},
				SpeedCheck:          SpeedCheckConfig{Mode: []string{}},
			}
			if policy {
				cfg.ProxyServer = []NameServer{{Addr: "192.0.2.60:53"}}
				cfg.ProxyServerPolicy = []Policy{{Domain: "node.example", NameServers: []NameServer{shared}}}
			}
			rs := NewResolver(cfg)
			t.Cleanup(rs.Close)
			ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
			defer cancel()
			business := routingTestExchange(t, rs.Resolver, ctx, routingTestQuery("business.example", 1))
			if ip := business.Answer[0].(*D.A).A; !ip.Equal(net.IPv4(192, 0, 2, 99)) {
				t.Fatalf("business DNS did not use its rules-selected transport: %s", ip)
			}
			if got := proxy.queryCount.Load(); got != 2 {
				t.Fatalf("business resolver did not route both its bootstrap and target queries: %d", got)
			}
			mu.Lock()
			directCount := len(directQuestions)
			mu.Unlock()
			if directCount != 0 {
				t.Fatal("business resolver bootstrap bypassed its upstream rules")
			}

			node := routingTestExchange(t, rs.ProxyResolver, ctx, routingTestQuery("node.example", 2))
			if ip := node.Answer[0].(*D.A).A; !ip.Equal(net.IPv4(192, 0, 2, 77)) {
				t.Fatalf("node DNS inherited the business transport: %s", ip)
			}
			if got := proxy.queryCount.Load(); got != 2 {
				t.Fatalf("resolving a node's DNS server re-entered the proxy it must bootstrap: %d proxy queries", got)
			}
			mu.Lock()
			bootstrapCount, nodeCount := directQuestions["resolver.bootstrap.test."], directQuestions["node.example."]
			mu.Unlock()
			if bootstrapCount != 1 || nodeCount != 1 {
				t.Fatalf("node resolver reused a business bootstrap/cache or missed its direct path: bootstrap=%d node=%d", bootstrapCount, nodeCount)
			}

			// With no dedicated node DNS, executor uses this same independent
			// root. It must remain reachable before any proxy exists.
			routingTestExchange(t, rs.BootstrapResolver, ctx, routingTestQuery("fallback-node.example", 3))
			if got := proxy.queryCount.Load(); got != 2 {
				t.Fatal("fallback node bootstrap inherited business upstream routing")
			}
		})
	}
}

func TestDNSUpstreamRuleRoutingClosesPrivateBootstrap(t *testing.T) {
	rs := NewResolver(Config{UpstreamRuleRouting: true})
	businessBootstrap := rs.Resolver.defaultResolver
	rs.Close()
	for _, r := range []*Resolver{businessBootstrap, rs.BootstrapResolver} {
		_, err := r.ExchangeContext(context.Background(), routingTestQuery("closed-bootstrap.example", 1))
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("closing resolvers left a private bootstrap active: %v", err)
		}
	}
}
