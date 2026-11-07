package executor

import (
	"testing"

	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/config"
	"github.com/metacubex/mihomo/dns"
)

func TestDNSUpstreamRuleRoutingReportsAppliedConfiguration(t *testing.T) {
	preserveUpdateConfiguration(t)
	if GetGeneral().DNSUpstreamRuleRouting {
		t.Fatal("unconfigured upstream routing should be disabled")
	}
	for _, enabled := range []bool{true, false} {
		cfg := &config.Config{General: &config.General{DNSUpstreamRuleRouting: enabled}}
		publishUpdateConfiguration(cfg)
		cfg.General.DNSUpstreamRuleRouting = !enabled
		if got := GetGeneral().DNSUpstreamRuleRouting; got != enabled {
			t.Fatalf("API reported a mutable candidate flag: got %v want %v", got, enabled)
		}
	}
}

func TestDNSUpstreamRuleRoutingNodeBootstrapWithAndWithoutDNSService(t *testing.T) {
	oldOwner := dnsResolverOwner
	oldDefault, oldMapper, oldService := resolver.DefaultResolver, resolver.DefaultHostMapper, resolver.DefaultService
	oldProxy, oldDirect, oldHosts := resolver.ProxyServerHostResolver, resolver.DirectHostResolver, resolver.UseSystemHosts
	dnsResolverOwner = nil
	resolver.DefaultResolver, resolver.ProxyServerHostResolver, resolver.DirectHostResolver = nil, nil, nil
	t.Cleanup(func() {
		closeDNSResolvers()
		dnsResolverOwner = oldOwner
		resolver.DefaultResolver, resolver.DefaultHostMapper, resolver.DefaultService = oldDefault, oldMapper, oldService
		resolver.ProxyServerHostResolver, resolver.DirectHostResolver, resolver.UseSystemHosts = oldProxy, oldDirect, oldHosts
	})
	for _, localDNS := range []bool{true, false} {
		updateDNS(&config.DNS{
			Enable:                 localDNS,
			DNSUpstreamRuleRouting: true,
			NameServer:             []dns.NameServer{{Addr: "192.0.2.53:53"}},
			DefaultNameserver:      []dns.NameServer{{Addr: "192.0.2.54:53"}},
		}, false)
		if dnsResolverOwner == nil || resolver.ProxyServerHostResolver != dnsResolverOwner.BootstrapResolver {
			t.Fatal("upstream routing left node resolution dependent on business DNS")
		}
		if !localDNS && (resolver.DefaultResolver != nil || resolver.DefaultService != nil || resolver.DefaultHostMapper != nil) {
			t.Fatal("independent bootstrap unexpectedly enabled the local DNS service")
		}
	}
}
