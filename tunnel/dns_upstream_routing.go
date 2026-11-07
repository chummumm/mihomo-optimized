package tunnel

import (
	"context"
	"errors"
	"fmt"

	"github.com/metacubex/mihomo/component/resolver"
	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"
)

// ErrDNSUpstreamUnresolved means an upstream needs bootstrap I/O. Callers may
// defer it to that server's own exchange instead of blocking healthy siblings
// or a lazy fallback behind an unrelated resolver hostname.
var ErrDNSUpstreamUnresolved = errors.New("DNS upstream address is not cached")

func dnsUpstreamMetadata(network C.NetWork, endpoint string) (*C.Metadata, error) {
	metadata := &C.Metadata{Type: C.INNER, InName: "DNS-TRANSPORT", NetWork: network}
	if err := metadata.SetRemoteAddress(endpoint); err != nil {
		return nil, err
	}
	if metadata.DstPort == 0 {
		return nil, errors.New("DNS upstream requires a destination port")
	}
	return metadata, nil
}

// CachedUpstream performs no network I/O. Cached addresses use the bootstrap
// resolver's own freshness and routing scope; it never creates another TTL
// cache or serves an expired resolver address to make an IP rule match.
func (d *DNSDialer) CachedUpstream(ctx context.Context, network C.NetWork, endpoint, qname string) (*DNSRoutingPlan, error) {
	metadata, err := dnsUpstreamMetadata(network, endpoint)
	if err != nil {
		return nil, err
	}
	if !metadata.DstIP.IsValid() {
		ip, ok := resolver.CachedIPWithResolver(icontext.WithDNSBootstrap(ctx), metadata.Host, d.r)
		if !ok {
			return nil, ErrDNSUpstreamUnresolved
		}
		metadata.DstIP = ip.Unmap()
	}
	return d.matchUpstream(ctx, metadata, qname)
}

// PrepareUpstream freezes the route of a resolver connection independently of
// the business question. Only its own hostname, bootstrapped IP, port and
// protocol participate in matching; a website's fixed outbound/source rules
// must not become the DNS transport's destination or explicit exit.
func (d *DNSDialer) PrepareUpstream(ctx context.Context, network C.NetWork, endpoint, qname string) (*DNSRoutingPlan, error) {
	metadata, err := dnsUpstreamMetadata(network, endpoint)
	if err != nil {
		return nil, err
	}
	if !metadata.DstIP.IsValid() {
		ips, err := resolver.LookupIPWithResolver(icontext.WithDNSBootstrap(ctx), metadata.Host, d.r)
		if err != nil {
			return nil, fmt.Errorf("DNS upstream bootstrap: %w", err)
		}
		// Prefer a stable address from the same live RRset. Randomly choosing
		// one on every cache lookup fragments scopes across all upstream IPs.
		for _, ip := range ips {
			ip = ip.Unmap()
			if !metadata.DstIP.IsValid() || ip.Is4() {
				metadata.DstIP = ip
			}
			if ip.Is4() {
				break
			}
		}
	}
	return d.matchUpstream(ctx, metadata, qname)
}

func (d *DNSDialer) matchUpstream(ctx context.Context, metadata *C.Metadata, qname string) (*DNSRoutingPlan, error) {
	if !d.Automatic() {
		return nil, errors.New("explicit DNS transport cannot use automatic upstream routing")
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if !(metadata.DstIP.IsGlobalUnicast() || metadata.DstIP.IsLoopback() || metadata.DstIP.IsLinkLocalUnicast()) {
		return nil, errors.New("DNS upstream requires a unicast resolver address")
	}
	// The destination is already resolved using this transport's bootstrap.
	// Use the ordinary rule evaluator (including GEOIP/IP-CIDR), while keeping
	// DNS's recursion and UDP rejection guards. No rule may resolve via the
	// business DNS or silently skip a selected TCP-only proxy.
	helper := newRuleMatchHelperWithProcessSnapshot(metadata, false, true)
	helper.ResolveIP = func() {}
	proxy, rule, err := matchWithOptions(metadata, helper, ruleMatchOptions{dnsQuery: true, deferUDPCheck: true})
	if err != nil {
		return nil, err
	}
	route, err := unwrapDNSProxyRoute(proxy, rule, metadata, true, false)
	if err != nil {
		return nil, err
	}
	switch route.proxy.Type() {
	case C.Dns, C.Rematch, C.Pass, C.PassRule:
		return nil, fmt.Errorf("DNS upstream routing cannot use outbound type %s", route.proxy.Type())
	}
	route.qname = qname
	origin := icontext.DNSRoutingMetadata(ctx)
	if origin == nil {
		origin = &C.Metadata{Type: C.INNER, NetWork: metadata.NetWork}
	}
	return &DNSRoutingPlan{route: route, origin: origin.Clone(), upstream: metadata,
		epoch: DNSRoutingEpoch(), inbound: icontext.DNSRoutingInbound(ctx)}, nil
}
