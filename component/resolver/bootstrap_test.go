package resolver

import (
	"context"
	"net/netip"
	"testing"
)

type bootstrapTestResolver struct {
	Resolver
	ip    netip.Addr
	calls int
}

func (r *bootstrapTestResolver) LookupIP(context.Context, string) ([]netip.Addr, error) {
	r.calls++
	return []netip.Addr{r.ip}, nil
}
func (r *bootstrapTestResolver) LookupIPv4(ctx context.Context, host string) ([]netip.Addr, error) {
	return r.LookupIP(ctx, host)
}
func (r *bootstrapTestResolver) LookupIPv6(ctx context.Context, host string) ([]netip.Addr, error) {
	return r.LookupIP(ctx, host)
}
func (*bootstrapTestResolver) Invalid() bool { return true }

func TestResourceBootstrapDNSIsScopedAndPreservesProtocolResolver(t *testing.T) {
	oldSystem, oldBusiness, oldProxy, oldDirect, oldIPv6 := SystemResolver, DefaultResolver, ProxyServerHostResolver, DirectHostResolver, DisableIPv6
	t.Cleanup(func() {
		SystemResolver, DefaultResolver, ProxyServerHostResolver, DirectHostResolver, DisableIPv6 = oldSystem, oldBusiness, oldProxy, oldDirect, oldIPv6
	})
	system := &bootstrapTestResolver{ip: netip.MustParseAddr("192.0.2.1")}
	business := &bootstrapTestResolver{ip: netip.MustParseAddr("192.0.2.2")}
	private := &bootstrapTestResolver{ip: netip.MustParseAddr("192.0.2.3")}
	SystemResolver, DefaultResolver, ProxyServerHostResolver, DirectHostResolver = system, business, business, business
	DisableIPv6 = true
	ctx := WithBootstrapResolver(context.Background())
	for _, r := range []Resolver{nil, business, system} {
		ips, err := LookupIPWithResolver(ctx, "bootstrap.invalid", r)
		if err != nil || len(ips) != 1 || ips[0] != system.ip {
			t.Fatalf("startup used business DNS: %v %v", ips, err)
		}
	}
	if business.calls != 0 {
		t.Fatal("startup consulted business rules")
	}
	if _, err := LookupIPv6WithResolver(ctx, "bootstrap.invalid", business); err != ErrIPv6Disabled {
		t.Fatalf("IPv6 policy changed: %v", err)
	}
	ips, err := LookupIPWithResolver(ctx, "private.invalid", private)
	if err != nil || ips[0] != private.ip || private.calls != 1 {
		t.Fatal("protocol-owned remote DNS was replaced")
	}
	ips, err = LookupIPWithResolver(context.Background(), "normal.invalid", business)
	if err != nil || ips[0] != business.ip {
		t.Fatal("normal DNS behavior was modified")
	}
}
