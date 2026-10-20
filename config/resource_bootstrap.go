package config

import (
	"context"
	"errors"
	"io"
	"net"
	"net/netip"
	"sync"
	"time"

	"github.com/metacubex/mihomo/component/dialer"
	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/component/resource"
	C "github.com/metacubex/mihomo/constant"
	P "github.com/metacubex/mihomo/constant/provider"
	icontext "github.com/metacubex/mihomo/context"

	"go.yaml.in/yaml/v3"
)

// ResourceBootstrap changes only the route used to obtain missing startup
// resources. The normal runtime never keeps the temporary outbound adapters.
type ResourceBootstrap struct {
	Proxy         string `yaml:"proxy" json:"proxy"`
	ProxyProvider string `yaml:"proxy-provider" json:"proxy-provider"`
	Timeout       int64  `yaml:"timeout" json:"timeout"`
}

func (b *ResourceBootstrap) UnmarshalYAML(node *yaml.Node) error {
	if node.Kind != yaml.MappingNode {
		return resource.NewBootstrapError("invalid_config", "config", "resource-bootstrap", nil)
	}
	type plain ResourceBootstrap
	value := plain{Timeout: int64(resource.DefaultBootstrapTimeout / time.Second)}
	for i := 0; i < len(node.Content); i += 2 {
		if node.Content[i].Value == "timeout" && node.Content[i+1].Tag != "!!int" {
			return resource.NewBootstrapError("invalid_timeout", "config", "timeout", nil)
		}
	}
	if err := node.Decode(&value); err != nil {
		return resource.NewBootstrapError("invalid_config", "config", "resource-bootstrap", err)
	}
	if value.Timeout <= 0 || value.Timeout > resource.MaxBootstrapSeconds {
		return resource.NewBootstrapError("invalid_timeout", "config", "timeout", nil)
	}
	*b = ResourceBootstrap(value)
	return nil
}

func newResourceBootstrap(ctx context.Context, raw *RawConfig) (*resource.Bootstrap, error) {
	policy := raw.ResourceBootstrap
	if policy == nil {
		policy = &ResourceBootstrap{Timeout: int64(resource.DefaultBootstrapTimeout / time.Second)}
	}
	if policy.Timeout <= 0 || policy.Timeout > resource.MaxBootstrapSeconds {
		return nil, resource.NewBootstrapError("invalid_timeout", "config", "timeout", nil)
	}
	if policy.Proxy == "" && policy.ProxyProvider != "" {
		return nil, resource.NewBootstrapError("missing_proxy", "config", "proxy", nil)
	}
	rawSnapshot := *raw
	// Normal parsing later topologically sorts this slice in place. Preserve
	// configuration order for an implicit GLOBAL first built by an override.
	rawSnapshot.ProxyGroup = append([]map[string]any(nil), raw.ProxyGroup...)
	temporary := &bootstrapOutbound{raw: &rawSnapshot, policy: *policy, probeSlots: make(chan struct{}, 10)}
	var defaultDialer C.Dialer
	if policy.Proxy != "" {
		defaultDialer = temporary
	}
	scope := resource.NewBootstrap(ctx, time.Duration(policy.Timeout)*time.Second, defaultDialer, temporary, temporary)
	temporary.ctx = scope.Context()
	// Build only immutable descriptions and group policies. Protocol adapters
	// and probes stay lazy, so cached resources do not need a working network.
	if policy.Proxy != "" {
		if _, err := temporary.root(policy.Proxy, policy.ProxyProvider != ""); err != nil {
			_ = scope.Close()
			return nil, err
		}
	}
	return scope, nil
}

// bootstrapOutbound owns the local graph, not the live tunnel. Selection state
// is serialized separately from protocol construction, connections and teardown.
// No provider Initial or normal group DialContext is called in this graph.
type bootstrapOutbound struct {
	ctx                      context.Context
	raw                      *RawConfig
	policy                   ResourceBootstrap
	mu                       sync.Mutex
	proxies                  map[string]C.Proxy
	groups                   map[string]map[string]any
	providers                map[string]P.ProxyProvider
	building                 map[string]bool
	allProxies, allProviders []string
	selected                 map[string]string
	initialized              bool
	probeSlots               chan struct{}
	ownMu                    sync.Mutex
	closed                   bool
	owned                    []io.Closer
}

func (b *bootstrapOutbound) failure(code, name string, err error) error {
	return resource.NewBootstrapError(code, "proxy", name, err)
}

func (b *bootstrapOutbound) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	return bootstrapNamedDialer{owner: b, name: b.policy.Proxy, fromProvider: b.policy.ProxyProvider != ""}.DialContext(ctx, network, address)
}

func (b *bootstrapOutbound) ListenPacket(ctx context.Context, network, address string, destination netip.AddrPort) (net.PacketConn, error) {
	return bootstrapNamedDialer{owner: b, name: b.policy.Proxy, fromProvider: b.policy.ProxyProvider != ""}.ListenPacket(ctx, network, address, destination)
}

func (b *bootstrapOutbound) own(closer io.Closer) error {
	b.ownMu.Lock()
	if b.closed {
		b.ownMu.Unlock()
		_ = closer.Close()
		return context.Canceled
	}
	b.owned = append(b.owned, closer)
	b.ownMu.Unlock()
	return nil
}

func (b *bootstrapOutbound) Close() error {
	b.ownMu.Lock()
	b.closed = true
	owned := b.owned
	b.owned = nil
	b.ownMu.Unlock()
	var err error
	for i := len(owned) - 1; i >= 0; i-- {
		err = errors.Join(err, owned[i].Close())
	}
	return err
}

func (b *bootstrapOutbound) socketDialer(opts []dialer.Option) C.Dialer {
	// Preserve the node/protocol's socket options (including UDP reuse) and use
	// current system DNS; a previous runtime's resolver can be closed on reload.
	options := []dialer.Option{dialer.WithDefaults(b.raw.Interface, b.raw.RoutingMark)}
	return bootstrapSocketDialer{owner: b, options: append(options, opts...)}
}

type bootstrapSocketDialer struct {
	owner   *bootstrapOutbound
	options []dialer.Option
}

func (d bootstrapSocketDialer) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	ctx = resolver.WithBootstrapResolver(icontext.WithDNSBootstrap(ctx))
	c, err := dialer.DialContext(ctx, network, address, d.options...)
	if err != nil {
		return nil, err
	}
	if err := d.owner.own(c); err != nil {
		return nil, err
	}
	return c, nil
}

func (d bootstrapSocketDialer) ListenPacket(ctx context.Context, network, address string, destination netip.AddrPort) (net.PacketConn, error) {
	c, err := dialer.ListenPacket(ctx, network, address, destination, d.options...)
	if err != nil {
		return nil, err
	}
	if err := d.owner.own(c); err != nil {
		return nil, err
	}
	return c, nil
}
