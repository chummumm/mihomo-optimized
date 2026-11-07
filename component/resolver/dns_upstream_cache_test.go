package resolver

import (
	"context"
	"net/netip"
	"testing"

	"github.com/metacubex/mihomo/component/trie"
)

type upstreamCacheTestResolver struct {
	Resolver
	ip                        netip.Addr
	valid                     bool
	cachedCalls, networkCalls int
}

func (r *upstreamCacheTestResolver) Invalid() bool { return r.valid }
func (r *upstreamCacheTestResolver) LookupIPCached(context.Context, string) (netip.Addr, bool) {
	r.cachedCalls++
	return r.ip, r.ip.IsValid()
}
func (r *upstreamCacheTestResolver) LookupIP(context.Context, string) ([]netip.Addr, error) {
	r.networkCalls++
	return nil, ErrIPNotFound
}
func (r *upstreamCacheTestResolver) LookupIPv4(ctx context.Context, host string) ([]netip.Addr, error) {
	return r.LookupIP(ctx, host)
}
func (r *upstreamCacheTestResolver) LookupIPv6(ctx context.Context, host string) ([]netip.Addr, error) {
	return r.LookupIP(ctx, host)
}

func TestDNSUpstreamCachedResolverSelection(t *testing.T) {
	oldSystem, oldBusiness, oldProxy, oldDirect := SystemResolver, DefaultResolver, ProxyServerHostResolver, DirectHostResolver
	oldHosts, oldIPv6, oldUseSystemHosts := DefaultHosts, DisableIPv6, UseSystemHosts
	t.Cleanup(func() {
		SystemResolver, DefaultResolver, ProxyServerHostResolver, DirectHostResolver = oldSystem, oldBusiness, oldProxy, oldDirect
		DefaultHosts, DisableIPv6, UseSystemHosts = oldHosts, oldIPv6, oldUseSystemHosts
	})
	DefaultHosts, DisableIPv6, UseSystemHosts = NewHosts(trie.New[HostValue]()), true, false
	system := &upstreamCacheTestResolver{ip: netip.MustParseAddr("192.0.2.1"), valid: true}
	business := &upstreamCacheTestResolver{ip: netip.MustParseAddr("192.0.2.2"), valid: true}
	private := &upstreamCacheTestResolver{ip: netip.MustParseAddr("192.0.2.3"), valid: true}
	invalid := &upstreamCacheTestResolver{ip: netip.MustParseAddr("192.0.2.4")}
	SystemResolver, DefaultResolver, ProxyServerHostResolver, DirectHostResolver = system, business, business, business
	ordinary, bootstrap := context.Background(), WithBootstrapResolver(context.Background())
	for _, test := range []struct {
		ctx context.Context
		r   Resolver
		ip  netip.Addr
	}{
		{ordinary, nil, system.ip},
		{ordinary, invalid, system.ip},
		{ordinary, business, business.ip},
		{bootstrap, business, system.ip},
		{bootstrap, nil, system.ip},
		{bootstrap, private, private.ip},
	} {
		if ip, ok := CachedIPWithResolver(test.ctx, "cache.invalid", test.r); !ok || ip != test.ip {
			t.Fatalf("wrong cached resolver selection: got %v/%v want %s", ip, ok, test.ip)
		}
	}
	uncached := &bootstrapTestResolver{ip: netip.MustParseAddr("192.0.2.5")}
	if _, ok := CachedIPWithResolver(ordinary, "cache.invalid", uncached); ok || uncached.calls != 0 {
		t.Fatal("an unsupported cache lookup started a regular DNS lookup")
	}
	for _, r := range []*upstreamCacheTestResolver{system, business, private, invalid} {
		if r.networkCalls != 0 {
			t.Fatal("cache-only resolver selection performed network resolution")
		}
	}
	if invalid.cachedCalls != 0 {
		t.Fatal("an invalid resolver was used instead of the system fallback")
	}
}

func TestDNSUpstreamCachedHostsAndAddressFamilies(t *testing.T) {
	oldHosts, oldIPv6, oldUseSystemHosts := DefaultHosts, DisableIPv6, UseSystemHosts
	t.Cleanup(func() { DefaultHosts, DisableIPv6, UseSystemHosts = oldHosts, oldIPv6, oldUseSystemHosts })
	tree := trie.New[HostValue]()
	value, err := NewHostValue([]string{"2001:db8::1", "192.0.2.10", "192.0.2.11"})
	if err != nil {
		t.Fatal(err)
	}
	if err := tree.Insert("cache.example", value); err != nil {
		t.Fatal(err)
	}
	alias, err := NewHostValue([]string{"cache.example"})
	if err != nil {
		t.Fatal(err)
	}
	if err := tree.Insert("alias.example", alias); err != nil {
		t.Fatal(err)
	}
	DefaultHosts, DisableIPv6, UseSystemHosts = NewHosts(tree), true, false
	r := &upstreamCacheTestResolver{valid: true}
	for _, host := range []string{"cache.example", "alias.example", "192.0.2.10", "::ffff:192.0.2.10"} {
		if ip, ok := CachedIPWithResolver(context.Background(), host, r); !ok || ip != netip.MustParseAddr("192.0.2.10") {
			t.Fatalf("hosts/literal lookup lost stable IPv4 preference for %q: %v/%v", host, ip, ok)
		}
	}
	if _, ok := CachedIPWithResolver(context.Background(), "2001:db8::1", r); ok {
		t.Fatal("cache-only literal lookup ignored disabled IPv6")
	}
	DisableIPv6 = false
	if ip, ok := CachedIPWithResolver(context.Background(), "2001:db8::1", r); !ok || ip != netip.MustParseAddr("2001:db8::1") {
		t.Fatal("enabled IPv6 literal was not available without DNS")
	}
	if r.cachedCalls != 0 || r.networkCalls != 0 {
		t.Fatal("static hosts/literals consulted a DNS resolver")
	}
}
