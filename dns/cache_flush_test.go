package dns

import (
	"context"
	"fmt"
	"testing"

	D "github.com/miekg/dns"
)

func TestResolversClearCacheIncludesDefaultAndBootstrap(t *testing.T) {
	for _, algorithm := range []string{"lru", "arc"} {
		for _, upstreamRouting := range []bool{false, true} {
			t.Run(fmt.Sprintf("%s/upstream-routing=%v", algorithm, upstreamRouting), func(t *testing.T) {
				rs := NewResolver(Config{
					Main:                []NameServer{{Addr: "192.0.2.1:53"}},
					Default:             []NameServer{{Addr: "192.0.2.2:53"}},
					ProxyServer:         []NameServer{{Addr: "192.0.2.3:53"}},
					DirectServer:        []NameServer{{Addr: "192.0.2.4:53"}},
					CacheAlgorithm:      algorithm,
					CacheMaxSize:        8,
					UpstreamRuleRouting: upstreamRouting,
				})
				t.Cleanup(rs.Close)
				query := new(D.Msg).SetQuestion("flush-cache.example.", D.TypeA)
				key := dnsCacheKey(context.Background(), query.Question[0])
				answer := cacheTestReply(query, 60)
				contexts := make(map[*Resolver]context.Context)
				for _, r := range []*Resolver{rs.Resolver, rs.ProxyResolver, rs.DirectResolver, rs.Resolver.defaultResolver, rs.BootstrapResolver} {
					ctx := r.cacheControl.Context(context.Background())
					contexts[r] = ctx
					if !r.cacheControl.Store(ctx, key, query.Question[0], answer) {
						t.Fatal("could not seed an owned DNS cache")
					}
				}

				rs.ClearCache()

				for r, oldContext := range contexts {
					if _, _, hit := r.cache.GetWithExpire(key); hit {
						t.Fatal("flush left an answer in an owned DNS cache")
					}
					if r.cacheControl.Store(oldContext, key, query.Question[0], answer) {
						t.Fatal("an in-flight query repopulated a flushed DNS cache")
					}
					if !r.cacheControl.Store(r.cacheControl.Context(context.Background()), key, query.Question[0], answer) {
						t.Fatal("flush prevented new queries from caching their answers")
					}
				}
			})
		}
	}
}
