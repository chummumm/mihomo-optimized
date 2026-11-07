package config

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/metacubex/mihomo/dns"
)

func TestDNSUpstreamRuleRoutingConfiguration(t *testing.T) {
	for _, test := range []struct {
		name, field string
		want        bool
	}{
		{"omitted", "", false},
		{"disabled", "dns-upstream-rule-routing: false\n", false},
		{"enabled", "dns-upstream-rule-routing: true\n", true},
	} {
		t.Run(test.name, func(t *testing.T) {
			raw, err := UnmarshalRawConfig([]byte(test.field + "dns:\n  nameserver: [192.0.2.53]\n  default-nameserver: [192.0.2.54]\n"))
			if err != nil {
				t.Fatal(err)
			}
			general, err := parseGeneral(raw)
			if err != nil {
				t.Fatal(err)
			}
			parsed, err := parseDNS(raw, nil)
			if err != nil {
				t.Fatal(err)
			}
			if general.DNSUpstreamRuleRouting != test.want || parsed.DNSUpstreamRuleRouting != test.want {
				t.Fatalf("upstream routing flag did not reach both configurations: general=%v dns=%v", general.DNSUpstreamRuleRouting, parsed.DNSUpstreamRuleRouting)
			}
			if general.DNSRuleRouting || parsed.DNSRuleRouting {
				t.Fatal("upstream routing unexpectedly enabled business-domain routing")
			}
			wire, err := json.Marshal(general)
			if err != nil {
				t.Fatal(err)
			}
			var object map[string]any
			if err := json.Unmarshal(wire, &object); err != nil {
				t.Fatal(err)
			}
			if object["dns-upstream-rule-routing"] != test.want {
				t.Fatalf("API configuration omitted the effective boolean: %s", wire)
			}
		})
	}
}

func TestDNSUpstreamRuleRoutingPreservesPoliciesAndExplicitExits(t *testing.T) {
	raw, err := UnmarshalRawConfig([]byte(`
dns-upstream-rule-routing: true
dns:
  enable: true
  respect-rules: true
  nameserver:
    - https://dns.example/dns-query#fixed-proxy
    - 192.0.2.54#RULES
    - 192.0.2.55#test-interface
    - 192.0.2.56
  nameserver-policy:
    "+.service.example": 192.0.2.57#DIRECT
  direct-nameserver:
    - https://direct-dns.example/dns-query#ecs=203.0.113.0/24
  direct-nameserver-follow-policy: true
  fallback: [192.0.2.58]
  fallback-filter:
    geoip: false
    domain: ["+.fallback.example"]
  default-nameserver: [192.0.2.53]
  proxy-server-nameserver: [https://node-dns.example/dns-query]
  proxy-server-nameserver-policy:
    "+.node.example": 192.0.2.59
`))
	if err != nil {
		t.Fatal(err)
	}
	for _, enabled := range []bool{true, false, true} {
		raw.DNSUpstreamRuleRouting = enabled
		parsed, err := parseDNS(raw, nil)
		if err != nil {
			t.Fatal(err)
		}
		for i, want := range []string{"fixed-proxy", dns.RespectRules, "test-interface", dns.RespectRules} {
			if got := parsed.NameServer[i].ProxyName; got != want {
				t.Fatalf("upstream flag=%v rewrote nameserver %d exit: got %q want %q", enabled, i, got, want)
			}
		}
		if parsed.DirectNameServer[0].ProxyName != "" || parsed.DefaultNameserver[0].ProxyName != "" || parsed.ProxyServerNameserver[0].ProxyName != "" {
			t.Fatal("automatic transport routing was injected into parsed bootstrap or direct exits")
		}
		if len(parsed.NameServerPolicy) != 1 || !parsed.DirectFollowPolicy || len(parsed.FallbackDomainFilter) != 1 || len(parsed.ProxyServerPolicy) != 1 {
			t.Fatal("upstream routing changed business-domain or node DNS policies")
		}
		if parsed.NameServerPolicy[0].NameServers[0].ProxyName != "DIRECT" || parsed.ProxyServerPolicy[0].NameServers[0].ProxyName != "" {
			t.Fatal("policy resolver exit semantics changed")
		}
		if parsed.DirectNameServer[0].Params["ecs"] != "203.0.113.0/24" {
			t.Fatal("upstream routing changed the configured ECS subnet")
		}
	}
}

func TestDNSUpstreamRuleRoutingAllowsIndependentNodeBootstrap(t *testing.T) {
	raw, err := UnmarshalRawConfig([]byte(`
dns-upstream-rule-routing: true
dns:
  enable: true
  respect-rules: true
  nameserver: [192.0.2.53]
  default-nameserver: [192.0.2.54]
`))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := parseDNS(raw, nil); err != nil {
		t.Fatalf("independent node bootstrap should allow respect-rules without a separate node pool: %v", err)
	}
	raw.DNSUpstreamRuleRouting = false
	if _, err := parseDNS(raw, nil); err == nil || !strings.Contains(err.Error(), "proxy-server-nameserver") {
		t.Fatalf("disabling both routing features did not restore legacy bootstrap validation: %v", err)
	}
}
