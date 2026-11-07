package resolver

import (
	"context"
	"net/netip"
)

// CachedIPWithResolver returns an existing address without starting a DNS
// exchange. Resolver selection follows LookupIPWithResolver; resolvers without
// a cache-only lookup contract simply report a miss.
func CachedIPWithResolver(ctx context.Context, host string, r Resolver) (netip.Addr, bool) {
	if ctx.Err() != nil {
		return netip.Addr{}, false
	}
	if usesBootstrapResolver(ctx, r) {
		r = SystemResolver
	} else {
		if node, ok := DefaultHosts.Search(host, false); ok {
			var ipv6 netip.Addr
			for _, candidate := range node.IPs {
				ip := candidate.Unmap()
				if ip.Is4() {
					return ip, true
				}
				if !DisableIPv6 && ip.Is6() && !ipv6.IsValid() {
					ipv6 = ip
				}
			}
			if ipv6.IsValid() {
				return ipv6, true
			}
		}
		if r == nil || !r.Invalid() {
			r = SystemResolver
		}
	}
	if ip, err := netip.ParseAddr(host); err == nil {
		ip = ip.Unmap()
		if ip.Is4() || (!DisableIPv6 && ip.Is6()) {
			return ip, true
		}
		return netip.Addr{}, false
	}
	cached, ok := r.(interface {
		LookupIPCached(context.Context, string) (netip.Addr, bool)
	})
	if !ok {
		return netip.Addr{}, false
	}
	ip, hit := cached.LookupIPCached(ctx, host)
	ip = ip.Unmap()
	if !hit || !ip.IsValid() || (DisableIPv6 && ip.Is6()) {
		return netip.Addr{}, false
	}
	return ip, true
}
