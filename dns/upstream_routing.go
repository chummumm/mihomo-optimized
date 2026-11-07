package dns

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"net"
	"sort"
	"strings"
	"sync"
	"sync/atomic"

	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"
	"github.com/metacubex/mihomo/tunnel"
	D "github.com/miekg/dns"
)

type dnsUpstreamRouteKey struct{}

type dnsUpstreamPlan struct {
	plan     *tunnel.DNSRoutingPlan
	err      error
	pending  bool
	mu       sync.Mutex
	ready    bool
	running  chan struct{}
	network  C.NetWork
	endpoint string
	qname    string
}

func (p *dnsUpstreamPlan) resolve(ctx context.Context, dialer *dnsDialer) (*tunnel.DNSRoutingPlan, error) {
	if !p.pending {
		return p.plan, p.err
	}
	for {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		p.mu.Lock()
		if p.ready {
			plan, err := p.plan, p.err
			p.mu.Unlock()
			return plan, err
		}
		if done := p.running; done != nil {
			p.mu.Unlock()
			select {
			case <-done:
				continue
			case <-ctx.Done():
				return nil, ctx.Err()
			}
		}
		done := make(chan struct{})
		p.running = done
		p.mu.Unlock()
		plan, err := dialer.PrepareUpstream(ctx, p.network, p.endpoint, p.qname)
		p.mu.Lock()
		if canceled := ctx.Err(); canceled != nil {
			// Main/fallback or dual-stack branches can share this dialer.
			// Losing a race cancels only that branch; its bootstrap failure
			// must not poison a still-live sibling's routing snapshot.
			plan, err = nil, canceled
		} else {
			p.plan, p.err, p.ready = plan, err, true
		}
		p.running = nil
		close(done)
		p.mu.Unlock()
		return plan, err
	}
}

type dnsSystemUpstreams struct {
	clients []dnsClient
	err     error
}

// One immutable transport snapshot belongs to one resolver invocation. It is
// separate from dnsQueryRoute: the latter still owns business pool selection,
// source-aware rejection and destination-IP optimization.
type dnsUpstreamRoute struct {
	plans     map[*dnsDialer]*dnsUpstreamPlan
	systems   map[*systemClient]dnsSystemUpstreams
	key       string
	cacheable bool
	closed    atomic.Bool
}

func queryUpstreamRoute(ctx context.Context) *dnsUpstreamRoute {
	value, _ := ctx.Value(dnsUpstreamRouteKey{}).(*dnsUpstreamRoute)
	return value
}

// Freeze routes using only static addresses, hosts and fresh bootstrap cache.
// Unresolved hostnames are bootstrapped by their own exchange, never by a
// shared preparation barrier. A partially known scope cannot safely reuse or
// populate shared answers; the next query can use the warmed bootstrap cache.
func (r *Resolver) prepareDNSUpstreamRouting(ctx context.Context, message *D.Msg) (context.Context, error) {
	ctx = context.WithValue(ctx, dnsUpstreamRouteKey{}, (*dnsUpstreamRoute)(nil))
	if !r.upstreamRuleRouting || tunnel.Mode() != tunnel.Rule {
		return ctx, nil
	}
	state := &dnsUpstreamRoute{plans: make(map[*dnsDialer]*dnsUpstreamPlan), systems: make(map[*systemClient]dnsSystemUpstreams), cacheable: true}
	main, fallback := r.dnsQueryServers(ctx)
	if matched := r.matchPolicy(message); len(matched) != 0 {
		main, fallback = matched, nil
	} else if !isIPRequest(message.Question[0]) {
		fallback = nil
	} else if len(fallback) != 0 && r.shouldOnlyQueryFallback(message) {
		main, fallback = fallback, nil
	}
	var dialers []*dnsDialer
	var identities []string
	seen := make(map[*dnsDialer]bool)
	var collect func(dnsClient)
	collect = func(client dnsClient) {
		// Include the wrapped client identity: ECS/other DNS parameters may
		// share a raw transport but must not share their answer scope.
		identities = append(identities, fmt.Sprintf("client:%T:%p", client, client))
		for {
			wrapped, ok := client.(interface{ Unwrap() dnsClient })
			if !ok {
				break
			}
			client = wrapped.Unwrap()
		}
		if system, ok := client.(*systemClient); ok {
			if _, exists := state.systems[system]; exists {
				return
			}
			clients, err := system.getDnsClients()
			if len(clients) == 0 && len(system.defaultNS) != 0 && !errors.Is(err, net.ErrClosed) {
				clients, err = system.defaultNS, nil
			}
			state.systems[system] = dnsSystemUpstreams{clients: clients, err: err}
			if err != nil {
				identities = append(identities, "system-error:"+err.Error())
			}
			for _, nested := range clients {
				collect(nested)
			}
			return
		}
		_, dialer, network, endpoint := nativeClientDetails(client)
		if dialer == nil || !dialer.Automatic() || seen[dialer] {
			return
		}
		seen[dialer] = true
		state.plans[dialer] = &dnsUpstreamPlan{network: network, endpoint: endpoint}
		dialers = append(dialers, dialer)
	}
	for _, clients := range [][]dnsClient{main, fallback} {
		for _, client := range clients {
			collect(client)
		}
	}
	planning := ctx
	if business := queryRoute(ctx); business != nil && business.origin != nil {
		planning = icontext.WithDNSRoutingMetadata(ctx, business.origin)
	}
	qname := strings.ToLower(strings.TrimSuffix(message.Question[0].Name, "."))
	if qname == "" {
		qname = "."
	}
	var businessError error
	if business := queryRoute(ctx); business != nil {
		businessError = business.err
		if business.plan != nil && (business.plan.Type() == C.Reject || business.plan.Type() == C.RejectDrop) {
			businessError = errors.New("automatic native DNS business route is rejected")
		}
	}
	for _, dialer := range dialers {
		item := state.plans[dialer]
		item.qname = qname
		if businessError != nil {
			item.err = businessError
		} else {
			item.plan, item.err = dialer.CachedUpstream(planning, item.network, item.endpoint, qname)
			if errors.Is(item.err, tunnel.ErrDNSUpstreamUnresolved) {
				item.pending, state.cacheable = true, false
			}
		}
		identity := fmt.Sprintf("transport:%p:%s:", dialer, item.endpoint)
		if item.err != nil {
			identity += "error:" + item.err.Error()
		} else {
			identity += item.plan.TransportKey()
		}
		identities = append(identities, identity)
	}
	if err := ctx.Err(); err != nil {
		return ctx, err
	}
	sort.Strings(identities)
	hash := sha256.New()
	for _, identity := range identities {
		fmt.Fprintf(hash, "%d:%s", len(identity), identity)
	}
	state.key = fmt.Sprintf("%x", hash.Sum(nil))
	return context.WithValue(ctx, dnsUpstreamRouteKey{}, state), nil
}

func dnsUpstreamCacheable(ctx context.Context) bool {
	state := queryUpstreamRoute(ctx)
	return state == nil || state.cacheable
}

func dnsUpstreamCacheKey(ctx context.Context, key string) string {
	if route := queryUpstreamRoute(ctx); route != nil {
		return key + "|dns-upstream:" + route.key
	}
	return key
}
