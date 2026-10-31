package config

import (
	"fmt"

	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/dns"
	D "github.com/miekg/dns"
)

func parseDNSAnswerIPFamily(raw *RawConfig, existing dns.AnswerPolicy) (*dns.AnswerIPFamilyPolicy, error) {
	policy, err := dns.NewAnswerIPFamilyPolicy(raw.DNS.AnswerIPFamilyPolicy)
	if err != nil {
		return nil, fmt.Errorf("dns.%w", err)
	}
	for name, value := range raw.DNS.AnswerIPFamilyPolicy {
		family, _ := C.ParseTargetIPFamily(value)
		if family == C.TargetIPv6 && (!raw.IPv6 || !raw.DNS.IPv6 || existing.ForceAAAASOA || existing.ForceQtypeSOA.Contains(D.TypeAAAA)) ||
			family == C.TargetIPv4 && existing.ForceQtypeSOA.Contains(D.TypeA) {
			return nil, fmt.Errorf("dns.answer-ip-family-policy[%q]: %s is disabled by the existing DNS/global policy", name, family)
		}
	}
	return policy, nil
}
