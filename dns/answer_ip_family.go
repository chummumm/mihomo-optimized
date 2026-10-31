package dns

import (
	"context"
	"fmt"
	"slices"
	"strings"

	"github.com/metacubex/mihomo/component/dnsstats"
	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"

	D "github.com/miekg/dns"
)

// AnswerIPFamilyPolicy is an immutable client-output policy. It never changes
// the resolver's cached upstream facts or infers a future business outbound.
type AnswerIPFamilyPolicy struct {
	exact  map[string]C.TargetIPFamily
	suffix []answerIPFamilySuffix
}

type answerIPFamilySuffix struct {
	domain string
	family C.TargetIPFamily
}

func NewAnswerIPFamilyPolicy(entries map[string]string) (*AnswerIPFamilyPolicy, error) {
	if len(entries) == 0 {
		return nil, nil
	}
	policy := &AnswerIPFamilyPolicy{exact: make(map[string]C.TargetIPFamily)}
	seen := make(map[string]bool)
	for pattern, value := range entries {
		family, err := C.ParseTargetIPFamily(value)
		if err != nil || value == "" {
			return nil, fmt.Errorf("answer-ip-family-policy[%q]: expected any, ipv4 or ipv6", pattern)
		}
		suffix := strings.HasPrefix(pattern, "+.")
		domain := pattern
		if suffix {
			domain = strings.TrimPrefix(domain, "+.")
		}
		domain = strings.ToLower(strings.TrimSuffix(domain, "."))
		if !validAnswerPolicyDomain(domain) {
			return nil, fmt.Errorf("answer-ip-family-policy: invalid domain pattern %q; only exact names and +.suffix are supported", pattern)
		}
		key := domain
		if suffix {
			key = "+." + domain
		}
		if seen[key] {
			return nil, fmt.Errorf("answer-ip-family-policy: duplicate domain pattern %q", pattern)
		}
		seen[key] = true
		if suffix {
			policy.suffix = append(policy.suffix, answerIPFamilySuffix{domain, family})
		} else {
			policy.exact[domain] = family
		}
	}
	slices.SortFunc(policy.suffix, func(a, b answerIPFamilySuffix) int { return len(b.domain) - len(a.domain) })
	return policy, nil
}

func validAnswerPolicyDomain(domain string) bool {
	if domain == "" || len(domain) > 253 {
		return false
	}
	for _, label := range strings.Split(domain, ".") {
		if len(label) == 0 || len(label) > 63 {
			return false
		}
		for _, c := range label {
			if !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_') {
				return false
			}
		}
	}
	return true
}

func (p *AnswerIPFamilyPolicy) Family(name string) C.TargetIPFamily {
	if p == nil {
		return C.TargetIPAny
	}
	name = strings.ToLower(strings.TrimSuffix(name, "."))
	if family, ok := p.exact[name]; ok {
		return family
	}
	for _, entry := range p.suffix {
		if name == entry.domain || strings.HasSuffix(name, "."+entry.domain) {
			return entry.family
		}
	}
	return C.TargetIPAny
}

type answerIPFamilyKey struct{}

func answerIPFamily(ctx context.Context) C.TargetIPFamily {
	family, _ := ctx.Value(answerIPFamilyKey{}).(C.TargetIPFamily)
	return family
}

func familyBlocksQtype(family C.TargetIPFamily, qtype uint16) bool {
	return family == C.TargetIPv4 && qtype == D.TypeAAAA || family == C.TargetIPv6 && qtype == D.TypeA
}

func withAnswerIPFamilyPolicy(policy *AnswerIPFamilyPolicy) middleware {
	return func(next handler) handler {
		return func(ctx *icontext.DNSContext, query *D.Msg) (*D.Msg, error) {
			if icontext.DNSBootstrap(ctx) || len(query.Question) != 1 || query.Question[0].Qclass != D.ClassINET {
				return next(ctx, query)
			}
			family := policy.Family(query.Question[0].Name)
			if family == C.TargetIPAny {
				return next(ctx, query)
			}
			// Keep the original QNAME's decision through local hosts aliases.
			copyCtx := *ctx
			copyCtx.Context = context.WithValue(ctx.Context, answerIPFamilyKey{}, family)
			answer, err := next(&copyCtx, query)
			ctx.SetType(copyCtx.Type())
			if err != nil {
				return answer, err
			}
			result := applyAnswerIPFamily(query, answer, family)
			if result != answer || copyCtx.Type() == icontext.DNSTypeFakeIP && familyBlocksQtype(family, query.Question[0].Qtype) {
				markDNSOutcome(ctx, dnsstats.Local)
			}
			return result, nil
		}
	}
}

func applyAnswerIPFamily(query, answer *D.Msg, family C.TargetIPFamily) *D.Msg {
	if family == C.TargetIPAny || answer == nil || answer.Rcode != D.RcodeSuccess {
		return answer
	}
	result := answer.Copy()
	changed := false
	for _, section := range []*[]D.RR{&result.Answer, &result.Ns, &result.Extra} {
		filtered := (*section)[:0]
		for _, rr := range *section {
			if rr.Header().Class != D.ClassINET || rr.Header().Rrtype == D.TypeOPT {
				filtered = append(filtered, rr)
				continue
			}
			if familyBlocksQtype(family, rr.Header().Rrtype) {
				changed = true
				continue
			}
			var service *D.SVCB
			switch record := rr.(type) {
			case *D.HTTPS:
				service = &record.SVCB
			case *D.SVCB:
				service = record
			}
			if service != nil {
				rewritten, valid := filterServiceFamily(service, family)
				changed = changed || rewritten
				if !valid {
					continue
				}
			}
			filtered = append(filtered, rr)
		}
		*section = filtered
	}
	if !changed {
		return answer
	}
	if !answerPolicyCanRewrite(query, answer) {
		return answerFamilyRefused(query)
	}
	result.AuthenticatedData = false
	result.Authoritative = false
	// A locally manufactured negative must not acquire an upstream negative
	// lifetime, including through a retained CNAME or authority SOA.
	if familyBlocksQtype(family, query.Question[0].Qtype) || len(result.Answer) == 0 {
		zeroAnswerFamilyTTL(result)
		hasSOA := false
		for _, rr := range result.Ns {
			if rr.Header().Rrtype == D.TypeSOA {
				hasSOA = true
				break
			}
		}
		if !hasSOA {
			result.Ns = append(result.Ns, &D.SOA{Hdr: D.RR_Header{Name: query.Question[0].Name, Rrtype: D.TypeSOA, Class: D.ClassINET}, Ns: ".", Mbox: "."})
		}
	}
	return result
}

func filterServiceFamily(record *D.SVCB, family C.TargetIPFamily) (changed, valid bool) {
	blocked := D.SVCB_IPV6HINT
	if family == C.TargetIPv6 {
		blocked = D.SVCB_IPV4HINT
	}
	for _, value := range record.Value {
		if value.Key() == blocked {
			changed = true
			break
		}
	}
	if !changed {
		return false, true
	}
	if record.Priority == 0 {
		return true, false
	}
	values := make([]D.SVCBKeyValue, 0, len(record.Value))
	keys := make(map[D.SVCBKey]bool)
	for _, value := range record.Value {
		if value.Key() == blocked {
			continue
		}
		if keys[value.Key()] {
			return true, false
		}
		keys[value.Key()] = true
		if mandatory, ok := value.(*D.SVCBMandatory); ok {
			codes := make([]D.SVCBKey, 0, len(mandatory.Code))
			for _, code := range mandatory.Code {
				if code != blocked {
					codes = append(codes, code)
				}
			}
			if len(codes) == 0 {
				continue
			}
			value = &D.SVCBMandatory{Code: codes}
		}
		values = append(values, value)
	}
	for _, value := range values {
		if mandatory, ok := value.(*D.SVCBMandatory); ok {
			seen := make(map[D.SVCBKey]bool)
			for _, code := range mandatory.Code {
				if code == D.SVCB_MANDATORY || !keys[code] || seen[code] {
					return true, false
				}
				seen[code] = true
			}
		}
	}
	record.Value = values
	return true, true
}

func zeroAnswerFamilyTTL(answer *D.Msg) {
	for _, section := range [][]D.RR{answer.Answer, answer.Ns, answer.Extra} {
		for _, rr := range section {
			if rr.Header().Rrtype != D.TypeOPT {
				rr.Header().Ttl = 0
			}
			if soa, ok := rr.(*D.SOA); ok {
				soa.Minttl = 0
			}
		}
	}
}

func answerFamilyRefused(query *D.Msg) *D.Msg {
	answer := new(D.Msg).SetRcode(query, D.RcodeRefused)
	answer.RecursionAvailable = true
	answer.AuthenticatedData = false
	if opt := query.IsEdns0(); opt != nil {
		answer.SetEdns0(opt.UDPSize(), opt.Do())
		answer.IsEdns0().Option = append(answer.IsEdns0().Option, &D.EDNS0_EDE{InfoCode: D.ExtendedErrorCodeFiltered, ExtraText: "local answer IP family policy"})
	}
	return answer
}

func answerFamilyFakeNODATA(query *D.Msg) *D.Msg {
	if speedCheckQueryProtected(query) || query.CheckingDisabled || query.IsEdns0() != nil && query.IsEdns0().Do() {
		return answerFamilyRefused(query)
	}
	answer := new(D.Msg).SetReply(query)
	answer.RecursionAvailable = true
	answer.AuthenticatedData = false
	answer.Ns = []D.RR{&D.SOA{Hdr: D.RR_Header{Name: query.Question[0].Name, Rrtype: D.TypeSOA, Class: D.ClassINET}, Ns: ".", Mbox: "."}}
	return answer
}
