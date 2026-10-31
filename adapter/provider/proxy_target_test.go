package provider

import (
	"encoding/json"
	"testing"

	"github.com/metacubex/mihomo/adapter"
)

func TestProviderTargetFamilyUsesProviderConfigurationSnapshot(t *testing.T) {
	family := "ipv4"
	payload := []byte("proxies:\n  - {name: test, type: socks5, server: 127.0.0.1, port: 1080}\n")
	parse, err := NewProxiesParser("provider", nil, "", "", "", "", overrideSchema{TargetIPFamily: &family}, "", adapter.WithProxyForceIP(true))
	if err != nil {
		t.Fatal(err)
	}
	proxies, err := parse(payload)
	if err != nil {
		t.Fatal(err)
	}
	defer proxies[0].Close()
	data, err := proxies[0].MarshalJSON()
	if err != nil {
		t.Fatal(err)
	}
	var result map[string]any
	if err := json.Unmarshal(data, &result); err != nil {
		t.Fatal(err)
	}
	if result["target-ip-family"] != family || result["proxy-force-ip"] != true {
		t.Fatalf("provider lost target policy: %s", data)
	}
	parseDisabled, err := NewProxiesParser("provider", nil, "", "", "", "", overrideSchema{TargetIPFamily: &family}, "")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := parseDisabled(payload); err == nil {
		t.Fatal("provider silently ignored family without force-IP")
	}
	// Parsing another configuration does not mutate the first provider's
	// immutable options, including its future subscription refreshes.
	refreshed, err := parse(payload)
	if err != nil {
		t.Fatal(err)
	}
	defer refreshed[0].Close()
}
