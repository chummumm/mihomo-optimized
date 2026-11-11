package config

import (
	"encoding/json"
	"fmt"
	"strings"
	"testing"

	"github.com/metacubex/mihomo/dns"
)

func TestDNSRespectRulesSelectsUpstreamRoutingOnlyWithBusinessRouting(t *testing.T) {
	for _, ruleRouting := range []bool{false, true} {
		for _, respectRules := range []bool{false, true} {
			t.Run(fmt.Sprintf("business=%v/respect=%v", ruleRouting, respectRules), func(t *testing.T) {
				raw, err := UnmarshalRawConfig([]byte(fmt.Sprintf(`
dns-rule-routing: %v
dns:
  respect-rules: %v
  nameserver: [192.0.2.53]
  default-nameserver: [192.0.2.54]
  proxy-server-nameserver: [192.0.2.55]
`, ruleRouting, respectRules)))
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
				if parsed.UpstreamRuleRouting != (ruleRouting && respectRules) || general.DNSRuleRouting != ruleRouting || parsed.DNSRuleRouting != ruleRouting {
					t.Fatalf("incorrect effective routing settings: general=%+v DNS=%+v", general, parsed)
				}
				wantProxy := ""
				if respectRules {
					wantProxy = dns.RespectRules
				}
				if parsed.NameServer[0].ProxyName != wantProxy {
					t.Fatalf("respect-rules no longer preserves native nameserver semantics: got %q want %q", parsed.NameServer[0].ProxyName, wantProxy)
				}
				for _, value := range []any{raw, general} {
					wire, err := json.Marshal(value)
					if err != nil {
						t.Fatal(err)
					}
					if strings.Contains(string(wire), "dns-upstream-rule-routing") {
						t.Fatalf("removed configuration field is still exposed: %s", wire)
					}
				}
			})
		}
	}
}

func TestRemovedDNSUpstreamRuleRoutingIsNotAnAlias(t *testing.T) {
	raw, err := UnmarshalRawConfig([]byte(`
dns-upstream-rule-routing: true
dns-rule-routing: true
dns:
  respect-rules: false
  nameserver: [192.0.2.53]
  default-nameserver: [192.0.2.54]
`))
	if err != nil {
		t.Fatal(err)
	}
	parsed, err := parseDNS(raw, nil)
	if err != nil {
		t.Fatal(err)
	}
	if parsed.UpstreamRuleRouting || parsed.NameServer[0].ProxyName != "" {
		t.Fatal("removed YAML key silently enabled the replacement feature")
	}
}

func TestDNSRespectRulesPreservesPoliciesAndExplicitExits(t *testing.T) {
	raw, err := UnmarshalRawConfig([]byte(`
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
		raw.DNSRuleRouting = enabled
		parsed, err := parseDNS(raw, nil)
		if err != nil {
			t.Fatal(err)
		}
		for i, want := range []string{"fixed-proxy", dns.RespectRules, "test-interface", dns.RespectRules} {
			if got := parsed.NameServer[i].ProxyName; got != want {
				t.Fatalf("rule-routing=%v rewrote nameserver %d exit: got %q want %q", enabled, i, got, want)
			}
		}
		if parsed.DirectNameServer[0].ProxyName != "" || parsed.DefaultNameserver[0].ProxyName != "" || parsed.ProxyServerNameserver[0].ProxyName != "" {
			t.Fatal("automatic transport routing was injected into parsed bootstrap or direct exits")
		}
		wantFallbackDomain := 1
		if enabled {
			wantFallbackDomain = 0
		}
		if len(parsed.NameServerPolicy) != 1 || !parsed.DirectFollowPolicy || len(parsed.FallbackDomainFilter) != wantFallbackDomain || len(parsed.ProxyServerPolicy) != 1 {
			t.Fatal("routing changed active nameserver/node policies or inactive domain fallback")
		}
		if parsed.NameServerPolicy[0].NameServers[0].ProxyName != "DIRECT" || parsed.ProxyServerPolicy[0].NameServers[0].ProxyName != "" {
			t.Fatal("policy resolver exit semantics changed")
		}
		if parsed.DirectNameServer[0].Params["ecs"] != "203.0.113.0/24" {
			t.Fatal("routing changed the configured ECS subnet")
		}
	}
}

func TestDNSRespectRulesKeepsLegacyBootstrapValidation(t *testing.T) {
	raw, err := UnmarshalRawConfig([]byte(`
dns-rule-routing: true
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
		t.Fatalf("rule-routing's isolated node bootstrap should allow respect-rules without a separate node pool: %v", err)
	}
	raw.DNSRuleRouting = false
	if _, err := parseDNS(raw, nil); err == nil || !strings.Contains(err.Error(), "proxy-server-nameserver") {
		t.Fatalf("disabled rule-routing lost native bootstrap validation: %v", err)
	}
}
