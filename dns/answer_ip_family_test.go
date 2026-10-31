package dns

import (
	"context"
	"errors"
	"net/netip"
	"strings"
	"testing"

	"github.com/metacubex/mihomo/component/fakeip"
	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/component/trie"
	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"
	D "github.com/miekg/dns"
)

func TestAnswerIPFamilyMatching(t *testing.T) {
	p, err := NewAnswerIPFamilyPolicy(map[string]string{"+.example": "ipv4", "+.nested.example": "ipv6", "free.nested.example": "any"})
	if err != nil {
		t.Fatal(err)
	}
	for name, want := range map[string]C.TargetIPFamily{"EXAMPLE.": C.TargetIPv4, "x.example": C.TargetIPv4, "nested.example": C.TargetIPv6, "x.nested.example": C.TargetIPv6, "free.nested.example": C.TargetIPAny, "notexample": C.TargetIPAny} {
		if got := p.Family(name); got != want {
			t.Errorf("%s: %s, want %s", name, got, want)
		}
	}
	for _, entries := range []map[string]string{{"*.example": "ipv4"}, {"DOMAIN,example": "ipv4"}, {"+.example": "auto"}, {"example": ""}, {"Example": "ipv4", "example.": "ipv6"}} {
		if _, err := NewAnswerIPFamilyPolicy(entries); err == nil {
			t.Fatalf("accepted invalid policy: %v", entries)
		}
	}
}

func TestAnswerIPFamilyCopiesAndFiltersEverySection(t *testing.T) {
	q, a := answerPolicyMessage(t, D.TypeAAAA, "www.example. 60 IN CNAME alias.example.", "alias.example. 120 IN AAAA 2001:db8::1")
	soa, _ := D.NewRR("example. 120 IN SOA ns.example. hostmaster.example. 1 60 60 60 120")
	a.Ns = []D.RR{soa, a.Answer[1]}
	a.Extra = []D.RR{a.Answer[1]}
	before := a.String()
	got := applyAnswerIPFamily(q, a, C.TargetIPv4)
	if got.Rcode != D.RcodeSuccess || len(got.Answer) != 1 || got.Answer[0].Header().Rrtype != D.TypeCNAME || len(got.Ns) != 1 || len(got.Extra) != 0 {
		t.Fatalf("invalid filtered answer: %s", got)
	}
	if got.Answer[0].Header().Ttl != 0 || got.Ns[0].(*D.SOA).Minttl != 0 {
		t.Fatalf("local NODATA can be cached: %s", got)
	}
	if a.String() != before {
		t.Fatal("mutated shared upstream message")
	}
	if unchanged := applyAnswerIPFamily(q, a, C.TargetIPv6); unchanged != a {
		t.Fatal("copied an unchanged answer")
	}
}

func TestAnswerIPFamilyHTTPSAndSVCBHints(t *testing.T) {
	for _, rrtype := range []string{"HTTPS", "SVCB"} {
		for _, family := range []C.TargetIPFamily{C.TargetIPv4, C.TargetIPv6} {
			qtype := D.TypeHTTPS
			if rrtype == "SVCB" {
				qtype = D.TypeSVCB
			}
			q, a := answerPolicyMessage(t, qtype, "www.example. 60 IN "+rrtype+" 1 . mandatory=alpn,ipv4hint,ipv6hint alpn=h2 ipv4hint=192.0.2.1 ipv6hint=2001:db8::1")
			a.Extra = append(a.Extra, D.Copy(a.Answer[0]))
			before := a.String()
			got := applyAnswerIPFamily(q, a, family)
			blocked, allowed := "ipv6hint", "ipv4hint"
			if family == C.TargetIPv6 {
				blocked, allowed = allowed, blocked
			}
			if strings.Contains(got.String(), blocked) || !strings.Contains(got.String(), allowed) || !strings.Contains(got.String(), "alpn") {
				t.Fatalf("invalid service filtering: %s", got)
			}
			wire, err := got.Pack()
			if err != nil {
				t.Fatal(err)
			}
			if err := new(D.Msg).Unpack(wire); err != nil {
				t.Fatal(err)
			}
			if a.String() != before {
				t.Fatal("mutated a shared SvcParam")
			}
		}
	}
	q, a := answerPolicyMessage(t, D.TypeHTTPS, "www.example. 60 IN HTTPS 1 . mandatory=ipv6hint ipv6hint=2001:db8::1")
	got := applyAnswerIPFamily(q, a, C.TargetIPv4)
	if strings.Contains(got.String(), "mandatory") || len(got.Answer) != 1 {
		t.Fatalf("empty mandatory not removed: %s", got)
	}
}

func TestAnswerIPFamilyProtectedResponses(t *testing.T) {
	for _, protection := range []string{"DO", "CD", "AD", "RRSIG", "TC"} {
		t.Run(protection, func(t *testing.T) {
			q, a := answerPolicyMessage(t, D.TypeAAAA, "www.example. 60 IN AAAA 2001:db8::1")
			q.SetEdns0(1232, protection == "DO")
			switch protection {
			case "CD":
				q.CheckingDisabled = true
			case "AD":
				a.AuthenticatedData = true
			case "TC":
				a.Truncated = true
			case "RRSIG":
				a.Answer = append(a.Answer, &D.RRSIG{Hdr: D.RR_Header{Name: "www.example.", Rrtype: D.TypeRRSIG, Class: D.ClassINET}})
			}
			before := a.String()
			got := applyAnswerIPFamily(q, a, C.TargetIPv4)
			if got.Rcode != D.RcodeRefused || len(got.Answer) != 0 || len(got.Ns) != 0 || got.AuthenticatedData || got.IsEdns0() == nil {
				t.Fatalf("unsafe protected answer: %s", got)
			}
			opts := got.IsEdns0().Option
			if len(opts) != 1 || opts[0].(*D.EDNS0_EDE).InfoCode != D.ExtendedErrorCodeFiltered {
				t.Fatalf("missing filter reason: %s", got)
			}
			if a.String() != before {
				t.Fatal("mutated protected shared response")
			}
			if got := applyAnswerIPFamily(q, a, C.TargetIPv6); got != a {
				t.Fatal("changed permitted protected response")
			}
		})
	}
}

func TestAnswerIPFamilyKeepsResolverErrorsAndNegativeFacts(t *testing.T) {
	q, a := answerPolicyMessage(t, D.TypeAAAA)
	soa, _ := D.NewRR("example. 120 IN SOA ns.example. hostmaster.example. 1 60 60 60 120")
	a.Ns = []D.RR{soa}
	for _, code := range []int{D.RcodeSuccess, D.RcodeNameError, D.RcodeServerFailure} {
		a.Rcode = code
		if got := applyAnswerIPFamily(q, a, C.TargetIPv4); got != a {
			t.Fatal("changed an upstream negative fact")
		}
	}
	p, _ := NewAnswerIPFamilyPolicy(map[string]string{"+.example": "ipv4"})
	failure := errors.New("upstream timeout")
	h := withAnswerIPFamilyPolicy(p)(func(*icontext.DNSContext, *D.Msg) (*D.Msg, error) { return nil, failure })
	if _, err := h(icontext.NewDNSContext(context.Background()), q); !errors.Is(err, failure) {
		t.Fatal("lost upstream error", err)
	}
}

func TestAnswerIPFamilyOnlyChangesClientOutputNotCache(t *testing.T) {
	q, a := answerPolicyMessage(t, D.TypeAAAA, "www.example. 120 IN AAAA 2001:db8::1")
	client := &answerPolicyTestClient{answer: a}
	r := NewResolverFromClient(client)
	defer r.Close()
	r.answerPolicy.IPFamily, _ = NewAnswerIPFamilyPolicy(map[string]string{"+.example": "ipv4"})
	h := newHandler(r, &ResolverEnhancer{mode: C.DNSNormal, ipv6: true})
	got, err := h(icontext.NewDNSContext(context.Background()), q)
	if err != nil || len(got.Answer) != 0 {
		t.Fatalf("client not filtered: %v %v", got, err)
	}
	raw, err := r.ExchangeContext(context.Background(), q)
	if err != nil || len(raw.Answer) != 1 || raw.Answer[0].Header().Ttl == 0 || client.calls.Load() != 1 {
		t.Fatalf("raw cache was changed: %v %v", raw, err)
	}
	// A newly installed handler can remove the policy without waiting for a
	// synthetic negative cache to expire.
	r.answerPolicy.IPFamily = nil
	got, err = newHandler(r, &ResolverEnhancer{mode: C.DNSNormal, ipv6: true})(icontext.NewDNSContext(context.Background()), q)
	if err != nil || len(got.Answer) != 1 {
		t.Fatalf("reload retained derived NODATA: %v %v", got, err)
	}
}

func TestAnswerIPFamilyFollowsOriginalHostsAlias(t *testing.T) {
	saved := resolver.DefaultHosts
	t.Cleanup(func() { resolver.DefaultHosts = saved })
	hosts := trie.New[resolver.HostValue]()
	_ = hosts.Insert("original.example", resolver.HostValue{IsDomain: true, Domain: "alias.other"})
	resolver.DefaultHosts = resolver.NewHosts(hosts)
	p, _ := NewAnswerIPFamilyPolicy(map[string]string{"original.example": "ipv4", "alias.other": "ipv6"})
	terminal := func(_ *icontext.DNSContext, query *D.Msg) (*D.Msg, error) {
		a := new(D.Msg).SetReply(query)
		a.Answer = []D.RR{&D.AAAA{Hdr: D.RR_Header{Name: query.Question[0].Name, Rrtype: D.TypeAAAA, Class: D.ClassINET, Ttl: 60}, AAAA: netip.MustParseAddr("2001:db8::1").AsSlice()}}
		return a, nil
	}
	h := withAnswerIPFamilyPolicy(p)(withHosts(&ResolverEnhancer{mode: C.DNSNormal})(terminal))
	q := new(D.Msg).SetQuestion("original.example.", D.TypeAAAA)
	got, err := h(icontext.NewDNSContext(context.Background()), q)
	if err != nil || len(got.Answer) != 1 || got.Answer[0].Header().Rrtype != D.TypeCNAME || got.Question[0].Name != q.Question[0].Name {
		t.Fatalf("alias changed the selected family: %v %v", got, err)
	}
}

func TestAnswerIPFamilyBlocksFakeAllocationWithoutLookup(t *testing.T) {
	pool, err := fakeip.New(fakeip.Options{IPNet: netip.MustParsePrefix("198.18.0.1/16"), Size: 16})
	if err != nil {
		t.Fatal(err)
	}
	pool6, err := fakeip.New(fakeip.Options{IPNet: netip.MustParsePrefix("fd00::1/64"), Size: 16})
	if err != nil {
		t.Fatal(err)
	}
	p, _ := NewAnswerIPFamilyPolicy(map[string]string{"+.example": "ipv4"})
	endpoint := func(*icontext.DNSContext, *D.Msg) (*D.Msg, error) {
		t.Fatal("fake-IP issued a real DNS lookup")
		return nil, nil
	}
	h := withAnswerIPFamilyPolicy(p)(withFakeIP(&fakeip.Skipper{}, pool, pool6, 1)(endpoint))
	q := new(D.Msg).SetQuestion("www.example.", D.TypeAAAA)
	got, err := h(icontext.NewDNSContext(context.Background()), q)
	if err != nil || len(got.Answer) != 0 || got.Rcode != D.RcodeSuccess || got.Ns[0].Header().Ttl != 0 {
		t.Fatalf("forbidden fake family escaped: %v %v", got, err)
	}
	q.SetQuestion("www.example.", D.TypeA)
	got, err = h(icontext.NewDNSContext(context.Background()), q)
	if err != nil || len(got.Answer) != 1 || got.Answer[0].Header().Rrtype != D.TypeA {
		t.Fatalf("allowed fake family stopped: %v %v", got, err)
	}
}
