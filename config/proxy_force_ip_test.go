package config

import (
	"strings"
	"testing"

	C "github.com/metacubex/mihomo/constant"
)

func TestProxyForceIPConfigAndNormalCompatibility(t *testing.T) {
	for _, enabled := range []string{"false", "true"} {
		raw, err := UnmarshalRawConfig([]byte("proxy-force-ip: " + enabled + "\ndns:\n  enhanced-mode: normal\n"))
		if err != nil {
			t.Fatal(err)
		}
		general, err := parseGeneral(raw)
		if err != nil {
			t.Fatal(err)
		}
		if general.ProxyForceIP != (enabled == "true") {
			t.Fatal("general config lost force-IP value")
		}
		dnsConfig, err := parseDNS(raw, nil)
		if err != nil || dnsConfig.EnhancedMode != C.DNSNormal {
			t.Fatalf("normal mode was changed: %v %v", dnsConfig, err)
		}
		proxies, providers, err := parseProxies(raw)
		if err != nil {
			t.Fatal(err)
		}
		if source, ok := proxies["GLOBAL"].(interface{ ForceIPPolicy() bool }); !ok || source.ForceIPPolicy() != (enabled == "true") {
			t.Fatal("group lost immutable target policy snapshot")
		}
		if source, ok := proxies["DIRECT"].(interface{ ForceIPPolicy() bool }); ok && source.ForceIPPolicy() {
			t.Fatal("DIRECT inherited force-IP policy")
		}
		for _, proxy := range proxies {
			_ = proxy.Close()
		}
		for _, provider := range providers {
			if closer, ok := provider.(interface{ Close() error }); ok {
				_ = closer.Close()
			}
		}
	}
	for _, input := range []string{
		"proxies: [{name: test, type: socks5, server: 127.0.0.1, port: 1080, target-ip-family: ipv4}]",
		"proxy-force-ip: true\nproxy-groups: [{name: test, type: select, proxies: [DIRECT], target-ip-family: ipv4}]",
	} {
		raw, err := UnmarshalRawConfig([]byte(input))
		if err != nil {
			t.Fatal(err)
		}
		_, _, err = parseProxies(raw)
		if err == nil || !strings.Contains(err.Error(), "target-ip-family") {
			t.Fatalf("invalid target config accepted: %v", err)
		}
	}
}
