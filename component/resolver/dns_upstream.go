package resolver

import (
	"context"
	"strings"
)

type dnsUpstreamResolverKey struct{}

type dnsUpstreamResolverScope struct {
	host     string
	resolver Resolver
}

// WithDNSUpstreamResolver preserves the resolver assigned to one DNS transport
// target through proxy group selection. Only the force-IP target lookup uses
// this scope; physical proxy server lookups keep their own resolver.
func WithDNSUpstreamResolver(ctx context.Context, host string, r Resolver) context.Context {
	return context.WithValue(ctx, dnsUpstreamResolverKey{}, dnsUpstreamResolverScope{
		host:     strings.TrimSuffix(host, "."),
		resolver: r,
	})
}

// DNSUpstreamResolver returns the resolver for this DNS transport's hostname.
// A present nil resolver is intentional: it retains the DNS dialer's system
// resolver fallback instead of falling back to the business resolver.
func DNSUpstreamResolver(ctx context.Context, host string) (Resolver, bool) {
	scope, ok := ctx.Value(dnsUpstreamResolverKey{}).(dnsUpstreamResolverScope)
	host = strings.TrimSuffix(host, ".")
	if !ok || host == "" || !strings.EqualFold(host, scope.host) {
		return nil, false
	}
	return scope.resolver, true
}
