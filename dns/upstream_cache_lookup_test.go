package dns

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/tunnel"

	D "github.com/miekg/dns"
)

type upstreamCacheNoNetwork struct{ calls atomic.Int32 }

func (c *upstreamCacheNoNetwork) ExchangeContext(context.Context, *D.Msg) (*D.Msg, error) {
	c.calls.Add(1)
	return nil, errors.New("cache-only lookup must not exchange DNS")
}
func (*upstreamCacheNoNetwork) Address() string  { return "cache-only-test" }
func (*upstreamCacheNoNetwork) ResetConnection() {}

func seedUpstreamCache(t *testing.T, r *Resolver, host string, qtype uint16, expires time.Time, ips ...string) string {
	t.Helper()
	query := new(D.Msg).SetQuestion(D.Fqdn(host), qtype)
	answer := new(D.Msg).SetReply(query)
	for _, address := range ips {
		header := D.RR_Header{Name: query.Question[0].Name, Rrtype: qtype, Class: D.ClassINET, Ttl: 60}
		if qtype == D.TypeA {
			answer.Answer = append(answer.Answer, &D.A{Hdr: header, A: net.ParseIP(address)})
		} else {
			answer.Answer = append(answer.Answer, &D.AAAA{Hdr: header, AAAA: net.ParseIP(address)})
		}
	}
	key := r.cacheKey(context.Background(), query)
	r.cache.SetWithExpire(key, answer, expires)
	return key
}

func TestDNSUpstreamCachedLookupFreshExpiredAndNoRefresh(t *testing.T) {
	oldIPv6 := resolver.DisableIPv6
	resolver.DisableIPv6 = true
	t.Cleanup(func() { resolver.DisableIPv6 = oldIPv6 })
	client := &upstreamCacheNoNetwork{}
	r := NewResolverFromClient(client)
	r.bootstrap = true
	r.cacheControl.options.Prefetch = true
	t.Cleanup(r.Close)
	expires := time.Now().Add(time.Minute).Truncate(time.Second)
	key := seedUpstreamCache(t, r, "fresh.example", D.TypeA, expires, "192.0.2.2", "192.0.2.3")
	seedUpstreamCache(t, r, "expired.example", D.TypeA, time.Now().Add(-time.Minute), "192.0.2.4")
	for i := 0; i < 2; i++ {
		if ip, ok := r.LookupIPCached(context.Background(), "fresh.example"); !ok || ip != netip.MustParseAddr("192.0.2.2") {
			t.Fatalf("fresh cache lookup lost stable first address: %v/%v", ip, ok)
		}
	}
	for _, host := range []string{"expired.example", "missing.example"} {
		if ip, ok := r.LookupIPCached(context.Background(), host); ok || ip.IsValid() {
			t.Fatalf("non-fresh answer was used for %q: %v/%v", host, ip, ok)
		}
	}
	message, storedExpiry, ok := r.cache.GetWithExpire(key)
	if !ok || !storedExpiry.Equal(expires) || message.Answer[0].Header().Ttl != 60 {
		t.Fatal("cache-only lookup aged or extended the stored answer")
	}
	if client.calls.Load() != 0 || len(r.cacheControl.hot) != 0 || len(r.cacheControl.inflight) != 0 || len(r.cacheControl.pending) != 0 || r.cacheControl.scheduler != nil {
		t.Fatal("cache-only miss/hit started DNS or a background refresh")
	}
}

func TestDNSUpstreamCachedLookupHonorsLifecycleAndEligibility(t *testing.T) {
	client := &upstreamCacheNoNetwork{}
	r := NewResolverFromClient(client)
	r.bootstrap = true
	t.Cleanup(r.Close)
	seedUpstreamCache(t, r, "fresh.example", D.TypeA, time.Now().Add(time.Minute), "192.0.2.2")
	canceled, cancel := context.WithCancel(context.Background())
	cancel()
	if _, ok := r.LookupIPCached(canceled, "fresh.example"); ok {
		t.Fatal("canceled lookup used a cached address")
	}
	r.bootstrap, r.ruleRouting = false, true
	if _, ok := r.LookupIPCached(context.Background(), "fresh.example"); ok {
		t.Fatal("cache-only lookup entered an ordinary business-routing resolver")
	}
	r.bootstrap = true
	oldMode := tunnel.Mode()
	t.Cleanup(func() { tunnel.SetMode(oldMode) })
	newMode := tunnel.Rule
	if oldMode == tunnel.Rule {
		newMode = tunnel.Global
	}
	generation, epoch := r.cacheControl.generation, r.cacheControl.epoch
	tunnel.SetMode(newMode)
	if _, ok := r.LookupIPCached(context.Background(), "fresh.example"); ok {
		t.Fatal("cache-only lookup accepted an obsolete routing epoch")
	}
	if r.cacheControl.generation != generation || r.cacheControl.epoch != epoch {
		t.Fatal("read-only lookup reset the cache controller")
	}
	r.Close()
	if _, ok := r.LookupIPCached(context.Background(), "fresh.example"); ok || client.calls.Load() != 0 {
		t.Fatal("closed cache lookup returned an address or performed DNS")
	}
}

func TestDNSUpstreamCachedLookupIPv6AndRecursionBound(t *testing.T) {
	oldIPv6 := resolver.DisableIPv6
	t.Cleanup(func() { resolver.DisableIPv6 = oldIPv6 })
	client := &upstreamCacheNoNetwork{}
	r := NewResolverFromClient(client)
	r.bootstrap, r.ipv6 = true, false // Infrastructure follows the global policy.
	t.Cleanup(r.Close)
	seedUpstreamCache(t, r, "ipv6.example", D.TypeAAAA, time.Now().Add(time.Minute), "2001:db8::2", "2001:db8::3")
	resolver.DisableIPv6 = false
	if ip, ok := r.LookupIPCached(context.Background(), "ipv6.example"); !ok || ip != netip.MustParseAddr("2001:db8::2") {
		t.Fatalf("infrastructure lookup lost its globally enabled IPv6 cache: %v/%v", ip, ok)
	}
	resolver.DisableIPv6 = true
	if _, ok := r.LookupIPCached(context.Background(), "ipv6.example"); ok {
		t.Fatal("cache-only lookup ignored global IPv6 policy")
	}
	routingTestEnable(t)
	cyclic := &Resolver{bootstrap: true, upstreamRuleRouting: true, cache: Config{}.newCache()}
	cyclic.main = []dnsClient{newClient("cache-cycle.invalid:53", cyclic, "", nil, nil, "")}
	t.Cleanup(cyclic.Close)
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	if ip, ok := cyclic.LookupIPCached(ctx, "target.example"); ok || ip.IsValid() || ctx.Err() != nil {
		t.Fatalf("programmatic bootstrap cycle did not return a bounded cache miss: %v/%v, %v", ip, ok, ctx.Err())
	}
	if client.calls.Load() != 0 {
		t.Fatal("IPv6 cache lookup performed DNS")
	}
}
