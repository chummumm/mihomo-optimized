package tunnel

import (
	"context"
	"net/netip"
	"testing"

	C "github.com/metacubex/mihomo/constant"
)

type lanTestAdapter struct {
	*dnsProxyTestBase
	prefixes []netip.Prefix
	unwraps  int
}

func (a *lanTestAdapter) PrepareLAN(context.Context, netip.Addr) error { return nil }
func (a *lanTestAdapter) LANPreconnectPrefixes() []netip.Prefix        { return a.prefixes }
func (a *lanTestAdapter) Unwrap(*C.Metadata, bool) C.Proxy             { a.unwraps++; return nil }

func TestLANStaticRuleOrderAndPrefixCoverage(t *testing.T) {
	leaf := &lanTestAdapter{dnsProxyTestBase: newDNSProxyTestBase("lan", C.WireGuard, true)}
	proxies := map[string]C.Proxy{"lan": newDNSProxyTestProxy(leaf)}
	for _, tc := range []struct {
		name  string
		rules []string
		want  int
	}{
		{"prior catchall", []string{"MATCH,DIRECT", "IP-CIDR,192.168.1.0/24,lan,no-resolve"}, 0},
		{"two halves shadow", []string{"IP-CIDR,192.168.1.0/25,DIRECT,no-resolve", "IP-CIDR,192.168.1.128/25,DIRECT,no-resolve", "IP-CIDR,192.168.1.0/24,lan,no-resolve"}, 0},
		{"partial coverage", []string{"IP-CIDR,192.168.1.0/25,DIRECT,no-resolve", "IP-CIDR,192.168.1.0/24,lan,no-resolve"}, 1},
		{"domain disjoint", []string{"DOMAIN,example.com,DIRECT", "IP-CIDR,192.168.1.0/24,lan,no-resolve"}, 1},
		{"regex can match empty host", []string{"DOMAIN-REGEX,.*,DIRECT", "IP-CIDR,192.168.1.0/24,lan,no-resolve"}, 0},
		{"source unknown", []string{"SRC-IP-CIDR,192.168.0.0/16,DIRECT", "IP-CIDR,192.168.1.0/24,lan,no-resolve"}, 0},
		{"CGNAT excluded", []string{"IP-CIDR,100.64.0.0/10,lan,no-resolve"}, 0},
		{"default alone", []string{"MATCH,lan"}, 0},
		{"single target", []string{"IP-CIDR,192.168.1.7/32,lan,no-resolve"}, 1},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var rules []C.Rule
			for _, rule := range tc.rules {
				rules = append(rules, dnsProxyTestRule(t, rule))
			}
			intents := staticLANIntents(proxies, rules, true)
			if len(intents) != tc.want {
				t.Fatalf("intents = %d, want %d", len(intents), tc.want)
			}
			if tc.name == "single target" && intents[0].Target != netip.MustParseAddr("192.168.1.7") {
				t.Fatal("real host rule lost its exact destination")
			}
		})
	}
	leaf.prefixes = []netip.Prefix{netip.MustParsePrefix("0.0.0.0/0"), netip.MustParsePrefix("192.168.5.0/24")}
	if len(staticLANIntents(proxies, []C.Rule{dnsProxyTestRule(t, "MATCH,lan")}, true)) != 1 {
		t.Fatal("explicit native private route did not supply LAN intent")
	}
}

func TestLANPrefixNormalizationAndDynamicSelection(t *testing.T) {
	for _, value := range []string{"192.168.1.0/24", "::ffff:192.168.1.0/120", "fd42::/64"} {
		if !privateLANPrefix(netip.MustParsePrefix(value)) {
			t.Errorf("LAN prefix rejected: %s", value)
		}
	}
	if !coveredLANPrefix(netip.MustParsePrefix("::ffff:192.168.1.0/120"), []netip.Prefix{netip.MustParsePrefix("192.168.1.0/25"), netip.MustParsePrefix("192.168.1.128/25")}) {
		t.Fatal("mapped IPv4 union coverage differs from IPv4")
	}
	for _, tp := range []C.AdapterType{C.LoadBalance, C.URLTest, C.Fallback, C.Relay} {
		group := &lanTestAdapter{dnsProxyTestBase: newDNSProxyTestBase("dynamic", tp, true)}
		if stableLANOutbound(newDNSProxyTestProxy(group)) != nil || group.unwraps != 0 {
			t.Fatalf("dynamic group was speculatively selected: %v", tp)
		}
	}
}
