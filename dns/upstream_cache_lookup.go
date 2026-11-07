package dns

import (
	"context"
	"net/netip"
	"time"

	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/tunnel"

	D "github.com/miekg/dns"
)

type dnsCachedLookupDepthKey struct{}

const dnsCachedLookupDepthLimit = 8

// LookupIPCached reads only fresh infrastructure answers. Preparing their
// current transport identity never performs a DNS exchange; a missing nested
// bootstrap answer makes the whole lookup a miss rather than starting work.
func (r *Resolver) LookupIPCached(ctx context.Context, host string) (netip.Addr, bool) {
	if r == nil || r.cache == nil || (r.ruleRouting && !r.bootstrap) || ctx.Err() != nil {
		return netip.Addr{}, false
	}
	depth, _ := ctx.Value(dnsCachedLookupDepthKey{}).(int)
	if depth >= dnsCachedLookupDepthLimit {
		return netip.Addr{}, false
	}
	ctx = context.WithValue(ctx, dnsCachedLookupDepthKey{}, depth+1)
	epoch := tunnel.DNSRoutingEpoch()
	control := r.cacheControl
	var generation uint64
	if control != nil {
		control.mu.Lock()
		valid := !control.closed && control.epoch == epoch && control.lifetime.Err() == nil
		generation = control.generation
		control.mu.Unlock()
		if !valid {
			return netip.Addr{}, false
		}
	}
	lookup := func(qtype uint16) (netip.Addr, bool) {
		query := new(D.Msg).SetQuestion(D.Fqdn(host), qtype)
		queryCtx, local, err := r.prepareDNSRouting(ctx, query)
		if err != nil || local != nil {
			return netip.Addr{}, false
		}
		queryCtx, err = r.prepareDNSUpstreamRouting(queryCtx, query)
		if err != nil || !dnsUpstreamCacheable(queryCtx) || ctx.Err() != nil {
			return netip.Addr{}, false
		}
		key := r.cacheKey(queryCtx, query)
		if control != nil {
			control.mu.Lock()
			defer control.mu.Unlock()
			if control.closed || control.generation != generation || control.epoch != epoch || control.lifetime.Err() != nil {
				return netip.Addr{}, false
			}
		}
		if tunnel.DNSRoutingEpoch() != epoch {
			return netip.Addr{}, false
		}
		message, expires, hit := getMsgFromCache(r.cache, key)
		if !hit || message == nil || !expires.After(time.Now()) || message.Rcode != D.RcodeSuccess || message.Truncated ||
			tunnel.DNSRoutingEpoch() != epoch || ctx.Err() != nil {
			return netip.Addr{}, false
		}
		for _, candidate := range msgToIP(message) {
			ip := candidate.Unmap()
			if (qtype == D.TypeA && ip.Is4()) || (qtype == D.TypeAAAA && ip.Is6()) {
				return ip, true
			}
		}
		return netip.Addr{}, false
	}
	if ip, ok := lookup(D.TypeA); ok {
		return ip, true
	}
	// Infrastructure lookups use the global address-family policy, just like
	// LookupIPWithResolver; the local DNS listener's ipv6 flag does not apply.
	if !resolver.DisableIPv6 {
		return lookup(D.TypeAAAA)
	}
	return netip.Addr{}, false
}
