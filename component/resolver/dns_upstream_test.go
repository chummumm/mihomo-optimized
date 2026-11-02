package resolver

import (
	"context"
	"testing"
)

type dnsUpstreamTestResolver struct{ Resolver }

func TestDNSUpstreamResolverOnlyMatchesItsHostname(t *testing.T) {
	r := &dnsUpstreamTestResolver{}
	parent := context.Background()
	ctx := WithDNSUpstreamResolver(parent, "Resolver.Example.", r)
	for _, host := range []string{"resolver.example", "RESOLVER.EXAMPLE", "resolver.example."} {
		if got, ok := DNSUpstreamResolver(ctx, host); !ok || got != r {
			t.Fatalf("equivalent hostname %q lost its resolver: %T, %v", host, got, ok)
		}
	}
	for _, host := range []string{"other.example", "sub.resolver.example", "", "."} {
		if got, ok := DNSUpstreamResolver(ctx, host); ok || got != nil {
			t.Fatalf("unrelated hostname %q inherited the upstream resolver", host)
		}
	}
	if got, ok := DNSUpstreamResolver(parent, "resolver.example"); ok || got != nil {
		t.Fatal("upstream scope mutated its parent context")
	}
	if got, ok := DNSUpstreamResolver(WithDNSUpstreamResolver(parent, "", r), ""); ok || got != nil {
		t.Fatal("empty destination acquired an upstream resolver")
	}
}

func TestDNSUpstreamResolverPreservesExplicitSystemFallback(t *testing.T) {
	ctx := WithDNSUpstreamResolver(context.Background(), "resolver.example", nil)
	if got, ok := DNSUpstreamResolver(ctx, "resolver.example"); !ok || got != nil {
		t.Fatal("explicit nil resolver became an absent scope")
	}
	if got, ok := DNSUpstreamResolver(ctx, "proxy.example"); ok || got != nil {
		t.Fatal("system fallback escaped its upstream hostname")
	}
}

func TestDNSUpstreamResolverNestedScopeDoesNotLeakParentTarget(t *testing.T) {
	first, second := &dnsUpstreamTestResolver{}, &dnsUpstreamTestResolver{}
	parent := WithDNSUpstreamResolver(context.Background(), "first.example", first)
	child := WithDNSUpstreamResolver(parent, "second.example", second)
	if got, ok := DNSUpstreamResolver(child, "second.example"); !ok || got != second {
		t.Fatal("nested DNS transport did not use its own resolver")
	}
	if got, ok := DNSUpstreamResolver(child, "first.example"); ok || got != nil {
		t.Fatal("nested transport inherited the parent's target scope")
	}
	if got, ok := DNSUpstreamResolver(parent, "first.example"); !ok || got != first {
		t.Fatal("nested transport replaced its parent's resolver")
	}
}
