package outbound

import (
	"context"
	"errors"
	"fmt"
	"net/netip"

	"github.com/metacubex/mihomo/component/resolver"
	C "github.com/metacubex/mihomo/constant"
)

// LookupProxyTargets resolves only the permitted destination family. It keeps
// resolver ordering and never turns a failed lookup into a missing-family fact.
func LookupProxyTargets(ctx context.Context, host string, r resolver.Resolver, prefer C.DNSPrefer, family C.TargetIPFamily) ([]netip.Addr, error) {
	if ip, err := netip.ParseAddr(host); err == nil {
		ip = ip.Unmap()
		if err := family.Check(ip); err != nil {
			return nil, err
		}
		return []netip.Addr{ip}, nil
	}
	var ips []netip.Addr
	var err error
	switch {
	case family == C.TargetIPv4 || family == C.TargetIPAny && prefer == C.IPv4Only:
		ips, err = resolver.LookupIPv4WithResolver(ctx, host, r)
	case family == C.TargetIPv6 || family == C.TargetIPAny && prefer == C.IPv6Only:
		ips, err = resolver.LookupIPv6WithResolver(ctx, host, r)
	default:
		ips, err = resolver.LookupIPWithResolver(ctx, host, r)
	}
	if err != nil {
		if errors.Is(err, resolver.ErrIPNotFound) {
			return nil, fmt.Errorf("%w: resolve %s: %w", C.ErrNoProxyTarget, host, err)
		}
		return nil, fmt.Errorf("resolve proxy target %s: %w", host, err)
	}
	result := make([]netip.Addr, 0, len(ips))
	seen := make(map[netip.Addr]bool, len(ips))
	for _, ip := range ips {
		ip = ip.Unmap()
		if family.Allows(ip) && !resolver.IsFakeIP(ip) && !seen[ip] {
			result = append(result, ip)
			seen[ip] = true
		}
	}
	if len(result) == 0 {
		return nil, fmt.Errorf("%w: %w (%s, %s)", C.ErrNoProxyTarget, resolver.ErrIPNotFound, family, host)
	}
	if !resolver.PreferIPOrder(r) && family == C.TargetIPAny {
		v4, v6 := resolver.SortationAddr(result)
		if prefer == C.IPv6Prefer {
			result = append(v6, v4...)
		} else {
			result = append(v4, v6...)
		}
	}
	return result, nil
}

// ResolveProxyTarget is used by protocol-owned resolvers after their normal
// initialization. The selected node supplies r; callers must not replace a
// private resolver with DefaultResolver simply to force numeric encoding.
func ResolveProxyTarget(ctx context.Context, metadata *C.Metadata, r resolver.Resolver, prefer C.DNSPrefer) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if metadata.Host == "" {
		if resolver.IsFakeIP(metadata.DstIP) {
			return fmt.Errorf("fake-IP has no real proxy destination: %s", metadata.DstIP)
		}
		return metadata.TargetIPFamily.Check(metadata.DstIP)
	}
	ips, err := LookupProxyTargets(ctx, metadata.Host, r, prefer, metadata.TargetIPFamily)
	if err != nil {
		return err
	}
	metadata.DstIP = ips[0]
	metadata.TargetCandidates = ips
	return nil
}
