package config

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"sync"
	"time"

	"github.com/metacubex/mihomo/adapter"
	"github.com/metacubex/mihomo/adapter/outbound"
	"github.com/metacubex/mihomo/adapter/provider"
	"github.com/metacubex/mihomo/common/structure"
	"github.com/metacubex/mihomo/component/dialer"
	"github.com/metacubex/mihomo/component/proxydialer"
	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/component/resource"
	C "github.com/metacubex/mihomo/constant"
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
	if policy.Proxy == "" {
		if policy.ProxyProvider != "" {
			return nil, resource.NewBootstrapError("missing_proxy", "config", "proxy", nil)
		}
		return resource.NewBootstrap(ctx, time.Duration(policy.Timeout)*time.Second, nil, nil), nil
	}
	temporary := &bootstrapOutbound{raw: raw, policy: *policy, built: make(map[string]C.Dialer), visiting: make(map[string]bool)}
	scope := resource.NewBootstrap(ctx, time.Duration(policy.Timeout)*time.Second, temporary, temporary)
	temporary.ctx = scope.Context()
	// Validate the exact selected node and its local dependencies now. Adapter
	// construction remains lazy so a warm cache need not open protocol sessions.
	if err := temporary.validate(policy.Proxy, policy.ProxyProvider != "", make(map[string]bool)); err != nil {
		_ = scope.Close()
		return nil, err
	}
	return scope, nil
}

// bootstrapOutbound owns a small, private graph of adapters. It does not use
// tunnel.Proxies, provider.Initial, health checks, or mutable strategy groups.
type bootstrapOutbound struct {
	ctx            context.Context
	raw            *RawConfig
	policy         ResourceBootstrap
	providerNodes  []map[string]any
	providerLoaded bool
	built          map[string]C.Dialer
	visiting       map[string]bool
	mu             sync.Mutex
	ownMu          sync.Mutex
	closed         bool
	owned          []io.Closer
}

func (b *bootstrapOutbound) failure(code, name string, err error) error {
	return resource.NewBootstrapError(code, "proxy", name, err)
}

func (b *bootstrapOutbound) mapping(name string, fromProvider bool) (map[string]any, error) {
	if err := b.ctx.Err(); err != nil {
		return nil, resource.BootstrapFailure(b.ctx, err)
	}
	if !fromProvider {
		var found map[string]any
		for _, node := range b.raw.Proxy {
			if node["name"] == name {
				if found != nil {
					return nil, b.failure("ambiguous_proxy", name, nil)
				}
				found = node
			}
		}
		if found != nil {
			return found, nil
		}
		if name == "DIRECT" {
			return map[string]any{"name": name, "type": "direct"}, nil
		}
		for _, group := range b.raw.ProxyGroup {
			if group["name"] == name {
				return nil, b.failure("dynamic_proxy", name, nil)
			}
		}
	}
	if b.policy.ProxyProvider != "" {
		if !b.providerLoaded {
			mapping, ok := b.raw.ProxyProvider[b.policy.ProxyProvider]
			if !ok {
				return nil, b.failure("missing_provider", b.policy.ProxyProvider, nil)
			}
			nodes, err := provider.BootstrapProxyMappings(b.ctx, mapping)
			if err != nil {
				return nil, b.failure("provider_cache_unavailable", b.policy.ProxyProvider, err)
			}
			b.providerNodes, b.providerLoaded = nodes, true
		}
		var found map[string]any
		for _, node := range b.providerNodes {
			if node["name"] != name {
				continue
			}
			if found != nil {
				return nil, b.failure("ambiguous_proxy", name, nil)
			}
			found = node
		}
		if found != nil {
			return found, nil
		}
	}
	return nil, b.failure("proxy_unavailable", name, nil)
}

func (b *bootstrapOutbound) validate(name string, fromProvider bool, visiting map[string]bool) error {
	key := fmt.Sprintf("%t:%s", fromProvider, name)
	if visiting[key] {
		return b.failure("proxy_cycle", name, nil)
	}
	if len(visiting) >= 64 {
		return b.failure("proxy_chain_too_deep", name, nil)
	}
	visiting[key] = true
	defer delete(visiting, key)
	mapping, err := b.mapping(name, fromProvider)
	if err != nil {
		return err
	}
	typeName, _ := mapping["type"].(string)
	if typeName == "dns" || typeName == "rematch" {
		return b.failure("dynamic_proxy", name, nil)
	}
	parentName, _ := mapping["dialer-proxy"].(string)
	if parentName == "" {
		return nil
	}
	if typeName == "direct" {
		return b.failure("invalid_proxy_chain", name, nil)
	}
	return b.validate(parentName, false, visiting)
}

func (b *bootstrapOutbound) build(name string, fromProvider bool) (C.Dialer, error) {
	key := fmt.Sprintf("%t:%s", fromProvider, name)
	if existing := b.built[key]; existing != nil {
		return existing, nil
	}
	if b.visiting[key] {
		return nil, b.failure("proxy_cycle", name, nil)
	}
	if len(b.visiting) >= 64 {
		return nil, b.failure("proxy_chain_too_deep", name, nil)
	}
	b.visiting[key] = true
	defer delete(b.visiting, key)
	mapping, err := b.mapping(name, fromProvider)
	if err != nil {
		return nil, err
	}
	typeName, _ := mapping["type"].(string)
	if typeName == "dns" || typeName == "rematch" {
		return nil, b.failure("dynamic_proxy", name, nil)
	}
	parentName, _ := mapping["dialer-proxy"].(string)
	var parent C.Dialer
	if parentName != "" {
		parent, err = b.build(parentName, false)
		if err != nil {
			return nil, err
		}
	}
	var result C.Dialer
	if typeName == "direct" {
		option := outbound.DirectOption{}
		decoder := structure.NewDecoder(structure.Option{TagName: "proxy", WeaklyTypedInput: true, KeyReplacer: structure.DefaultKeyReplacer})
		if err := decoder.Decode(mapping, &option); err != nil {
			return nil, b.failure("invalid_proxy", name, err)
		}
		if parent != nil {
			return nil, b.failure("invalid_proxy_chain", name, nil)
		}
		base := outbound.NewDirectWithOption(option)
		result = b.socketDialer(base.DialOptions())
	} else {
		factory := func(opts []dialer.Option) C.Dialer {
			if parent != nil {
				return parent
			}
			return b.socketDialer(opts)
		}
		proxy, err := adapter.ParseProxy(mapping, adapter.WithDialerFactoryForAPI(factory))
		if err != nil {
			return nil, b.failure("invalid_proxy", name, err)
		}
		if err := b.own(proxy); err != nil {
			return nil, err
		}
		result = proxydialer.New(proxy, false)
	}
	b.built[key] = result
	return result, nil
}

func (b *bootstrapOutbound) outbound() (C.Dialer, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if err := b.ctx.Err(); err != nil {
		return nil, err
	}
	return b.build(b.policy.Proxy, b.policy.ProxyProvider != "")
}

func (b *bootstrapOutbound) own(closer io.Closer) error {
	b.ownMu.Lock()
	defer b.ownMu.Unlock()
	if b.closed {
		_ = closer.Close()
		return context.Canceled
	}
	b.owned = append(b.owned, closer)
	return nil
}

func (b *bootstrapOutbound) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	d, err := b.outbound()
	if err != nil {
		return nil, err
	}
	c, err := d.DialContext(resolver.WithBootstrapResolver(icontext.WithDNSBootstrap(ctx)), network, address)
	if err != nil {
		return nil, err
	}
	if err := b.own(c); err != nil {
		return nil, err
	}
	return c, nil
}

func (b *bootstrapOutbound) ListenPacket(ctx context.Context, network, address string, destination netip.AddrPort) (net.PacketConn, error) {
	d, err := b.outbound()
	if err != nil {
		return nil, err
	}
	c, err := d.ListenPacket(resolver.WithBootstrapResolver(icontext.WithDNSBootstrap(ctx)), network, address, destination)
	if err != nil {
		return nil, err
	}
	if err := b.own(c); err != nil {
		return nil, err
	}
	return c, nil
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
