package dns

import (
	"context"
	"fmt"
	"strings"

	D "github.com/miekg/dns"
)

type dnsQueryPoolKey struct{}

// Pool selection is independent of transport routing. Freeze it once per
// resolver invocation so capabilities, exchange, caches and background work
// cannot disagree about a policy override or a DIRECT pool's follow setting.
type dnsQueryPool struct {
	resolver       *Resolver
	name           string
	main, fallback []dnsClient
}

func queryPool(ctx context.Context) *dnsQueryPool {
	pool, _ := ctx.Value(dnsQueryPoolKey{}).(*dnsQueryPool)
	return pool
}

// A matching policy replaces the selected pool completely. In particular, a
// private resolver failure must never fall through to public main/fallback DNS.
func (r *Resolver) dnsPoolForQuery(message *D.Msg, direct bool, policy []dnsClient) *dnsQueryPool {
	pool := &dnsQueryPool{resolver: r, name: "main", main: r.main, fallback: r.fallback}
	if direct {
		pool.name, pool.main, pool.fallback = "direct", r.direct, nil
		if !r.directFollowPolicy {
			policy = nil
		}
	}
	if len(policy) != 0 {
		pool.name += "-policy"
		pool.main, pool.fallback = policy, nil
	} else if !isIPRequest(message.Question[0]) {
		pool.fallback = nil
	} else if len(pool.fallback) != 0 && r.shouldOnlyQueryFallback(message) {
		pool.name, pool.main, pool.fallback = "fallback", pool.fallback, nil
	}
	return pool
}

// Direct and policy queries never borrow other pools after a failure. The
// fallback selection below supports internal exchanges without preparation;
// ordinary queries always use the pool frozen by prepareDNSRouting.
func (r *Resolver) dnsQueryServers(ctx context.Context, message *D.Msg) (main, fallback []dnsClient) {
	if pool := queryPool(ctx); pool != nil && pool.resolver == r {
		return pool.main, pool.fallback
	}
	pool := r.dnsPoolForQuery(message, false, r.matchPolicy(message))
	return pool.main, pool.fallback
}

func dnsQueryCapabilities(message *D.Msg, main, fallback []dnsClient) (automatic, explicit bool) {
	sets := [][]dnsClient{main}
	// Non-IP questions do not consult fallback in exchangeWithoutCache.
	if isIPRequest(message.Question[0]) {
		sets = append(sets, fallback)
	}
	for _, clients := range sets {
		for _, client := range clients {
			auto, fixed := dnsRoutingCapability(client)
			automatic = automatic || auto
			explicit = explicit || fixed
		}
	}
	return
}

func (route *dnsQueryRoute) poolIdentity() string {
	return dnsPoolIdentity(route.pool, route.main, route.fallback)
}

func dnsPoolIdentity(name string, main, fallback []dnsClient) string {
	if name == "" {
		return ""
	}
	var identity strings.Builder
	identity.WriteString(name)
	for _, set := range [][]dnsClient{main, fallback} {
		identity.WriteByte('|')
		for _, client := range set {
			fmt.Fprintf(&identity, "%T:%p;", client, client)
		}
	}
	return identity.String()
}
