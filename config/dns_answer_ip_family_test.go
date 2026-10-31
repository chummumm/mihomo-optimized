package config

import (
	"testing"

	C "github.com/metacubex/mihomo/constant"
)

func TestDNSAnswerIPFamilyConfiguration(t *testing.T) {
	raw, err := UnmarshalRawConfig([]byte("ipv6: true\ndns:\n  ipv6: true\n  answer-ip-family-policy:\n    '+.example': ipv4\n    'private.example': ipv6\n"))
	if err != nil {
		t.Fatal(err)
	}
	cfg, err := parseDNS(raw, nil)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.AnswerPolicy.IPFamily.Family("www.example") != C.TargetIPv4 || cfg.AnswerPolicy.IPFamily.Family("private.example") != C.TargetIPv6 {
		t.Fatal("policy lost during parsing")
	}
	for _, input := range []string{
		"dns:\n  answer-ip-family-policy: {example: auto}",
		"dns:\n  answer-ip-family-policy: {example: ipv6}",
		"dns:\n  answer-ip-family-policy: {'*.example': ipv4}",
		"dns:\n  answer-ip-family-policy: {example: ipv4, example: any}",
		"dns:\n  answer-ip-family-policy: {Example: ipv4, example: any}",
		"dns:\n  force-qtype-SOA: [1]\n  answer-ip-family-policy: {example: ipv4}",
		"ipv6: true\ndns:\n  ipv6: true\n  force-AAAA-SOA: true\n  answer-ip-family-policy: {example: ipv6}",
	} {
		raw, err := UnmarshalRawConfig([]byte(input))
		if err == nil {
			_, err = parseDNS(raw, nil)
		}
		if err == nil {
			t.Errorf("invalid configuration accepted: %s", input)
		}
	}
}
