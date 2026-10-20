package config

import (
	"context"
	"io"
	"os"
	"slices"
	"strings"
	"sync"

	"github.com/metacubex/mihomo/adapter"
	"github.com/metacubex/mihomo/adapter/outbound"
	"github.com/metacubex/mihomo/adapter/outboundgroup"
	"github.com/metacubex/mihomo/adapter/provider"
	"github.com/metacubex/mihomo/common/structure"
	"github.com/metacubex/mihomo/component/dialer"
	"github.com/metacubex/mihomo/component/profile/cachefile"
	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/component/resource"
	C "github.com/metacubex/mihomo/constant"
	P "github.com/metacubex/mihomo/constant/provider"
	icontext "github.com/metacubex/mihomo/context"
	"github.com/metacubex/mihomo/log"
)

const bootstrapEmpty = "__resource_bootstrap_empty__"

// initialize records protocol descriptions without constructing clients. Normal
// ParseProxyGroup therefore sees the same names/types/filter inputs on warm and
// cold starts; opening AnyTLS/QUIC/WireGuard sessions is deferred until a dial.
func (b *bootstrapOutbound) initialize() error {
	if b.initialized {
		return nil
	}
	b.proxies = make(map[string]C.Proxy)
	b.groups = make(map[string]map[string]any)
	b.providers = make(map[string]P.ProxyProvider)
	b.building = make(map[string]bool)
	for _, entry := range []struct{ name, kind string }{
		{"DIRECT", "direct"}, {"REJECT", "reject"}, {"REJECT-DROP", "reject-drop"},
		{"COMPATIBLE", "compatible"}, {"PASS", "pass"}, {"PASS-RULE", "pass-rule"},
		{bootstrapEmpty, "reject"},
	} {
		b.proxies[entry.name] = b.lazyNode(map[string]any{"name": entry.name, "type": entry.kind}, "")
	}
	proxyList := []string{"DIRECT", "REJECT"}
	for _, mapping := range b.raw.Proxy {
		name, ok := mapping["name"].(string)
		if !ok || name == "" {
			return b.failure("invalid_proxy", "", nil)
		}
		if b.proxies[name] != nil {
			return b.failure("ambiguous_proxy", name, nil)
		}
		b.proxies[name] = b.lazyNode(mapping, "")
		b.allProxies = append(b.allProxies, name)
		proxyList = append(proxyList, name)
	}
	for _, mapping := range b.raw.ProxyGroup {
		name, ok := mapping["name"].(string)
		if !ok || name == "" {
			return b.failure("invalid_group", "", nil)
		}
		if b.proxies[name] != nil || b.groups[name] != nil {
			return b.failure("ambiguous_proxy", name, nil)
		}
		b.groups[name] = mapping
		proxyList = append(proxyList, name)
	}
	for name := range b.raw.ProxyProvider {
		b.allProviders = append(b.allProviders, name)
	}
	slices.Sort(b.allProxies)
	slices.Sort(b.allProviders)
	if b.groups["GLOBAL"] == nil {
		// Match the normal implicit GLOBAL selector and its configuration order.
		// DIRECT is an explicit member here, unlike an empty group's hidden fallback.
		b.groups["GLOBAL"] = map[string]any{"name": "GLOBAL", "type": "select", "proxies": proxyList}
	}
	if b.raw.Profile.StoreSelected {
		selected, err := cachefile.ReadSelectedSnapshot(b.ctx)
		if err != nil {
			// A separate -t process cannot read a writer-locked cache.db. Falling back
			// to configured default/first is deliberate and never writes that choice.
			log.Warnln("%s", b.failure("selection_cache_unavailable", "selected", err))
		} else {
			b.selected = selected
		}
	}
	b.initialized = true
	return nil
}

func bootstrapAdapterType(kind string) C.AdapterType {
	switch kind {
	case "ss":
		return C.Shadowsocks
	case "ssr":
		return C.ShadowsocksR
	case "gost-relay":
		return C.GostRelay
	case "reject-drop":
		return C.RejectDrop
	case "pass-rule":
		return C.PassRule
	}
	for tp := C.Direct; tp <= C.GostRelay; tp++ {
		if strings.EqualFold(tp.String(), kind) {
			return tp
		}
	}
	return C.AdapterType(-1)
}

func (b *bootstrapOutbound) lazyNode(mapping map[string]any, providerName string) C.Proxy {
	name, _ := mapping["name"].(string)
	kind, _ := mapping["type"].(string)
	return adapter.NewProxy(&bootstrapNode{
		Base:  outbound.NewBase(outbound.BaseOption{Name: name, Type: bootstrapAdapterType(kind), ProviderName: providerName, UDP: true}),
		owner: b, mapping: mapping, providerName: providerName,
	})
}

// cachedProvider deliberately treats only a genuinely absent HTTP cache as an
// empty local snapshot. Bad files, path/permission errors and bad formats are
// not disguised as an unavailable subscription.
func (b *bootstrapOutbound) cachedProvider(name string) (P.ProxyProvider, error) {
	if p := b.providers[name]; p != nil {
		return p, nil
	}
	mapping, ok := b.raw.ProxyProvider[name]
	if !ok {
		return nil, b.failure("missing_provider", name, nil)
	}
	nodes, err := provider.BootstrapProxyMappings(b.ctx, mapping)
	if err != nil && !(mapping["type"] == "http" && os.IsNotExist(err)) {
		return nil, b.failure("provider_cache_unavailable", name, err)
	}
	var proxies []C.Proxy
	for _, node := range nodes {
		proxies = append(proxies, b.lazyNode(node, name))
	}
	p, err := provider.NewBootstrapProvider(name, mapping, proxies)
	if err != nil {
		return nil, b.failure("provider_cache_unavailable", name, err)
	}
	b.providers[name] = p
	if closer, ok := p.(io.Closer); ok {
		if err := b.own(closer); err != nil {
			return nil, err
		}
	}
	return p, nil
}

func (b *bootstrapOutbound) lookup(name string, fromProvider bool) (C.Proxy, error) {
	if !fromProvider {
		if p := b.proxies[name]; p != nil {
			return p, nil
		}
		if mapping := b.groups[name]; mapping != nil {
			return b.group(name, mapping)
		}
	}
	if b.policy.ProxyProvider != "" {
		pd, err := b.cachedProvider(b.policy.ProxyProvider)
		if err != nil {
			return nil, err
		}
		var found C.Proxy
		for _, p := range pd.Proxies() {
			if p.Name() != name {
				continue
			}
			if found != nil {
				return nil, b.failure("ambiguous_proxy", name, nil)
			}
			found = p
		}
		if found != nil {
			return found, nil
		}
		if pd.Count() == 0 {
			return nil, b.failure("provider_cache_unavailable", b.policy.ProxyProvider, nil)
		}
	}
	return nil, b.failure("proxy_unavailable", name, nil)
}

func (b *bootstrapOutbound) group(name string, mapping map[string]any) (C.Proxy, error) {
	if b.building[name] {
		return nil, b.failure("proxy_cycle", name, nil)
	}
	if len(b.building) >= 64 {
		return nil, b.failure("proxy_chain_too_deep", name, nil)
	}
	b.building[name] = true
	defer delete(b.building, name)
	option := outboundgroup.GroupCommonOption{}
	decoder := structure.NewDecoder(structure.Option{TagName: "group", WeaklyTypedInput: true})
	if err := decoder.Decode(mapping, &option); err != nil {
		return nil, b.failure("invalid_group", name, err)
	}
	for _, child := range option.Proxies {
		if _, err := b.lookup(child, false); err != nil {
			return nil, err
		}
	}
	use := option.Use
	if option.IncludeAll || option.IncludeAllProviders {
		use = b.allProviders
	}
	for _, providerName := range use {
		if _, err := b.cachedProvider(providerName); err != nil {
			return nil, err
		}
	}
	local := make(map[string]any, len(mapping)+1)
	for key, value := range mapping {
		local[key] = value
	}
	if option.EmptyFallback == "" {
		// Runtime compatibility's implicit COMPATIBLE is a direct route. A startup
		// group with no usable local members must fail instead of leaking direct.
		local["empty-fallback"] = bootstrapEmpty
	} else if _, err := b.lookup(option.EmptyFallback, false); err != nil {
		return nil, err
	}
	before := b.providers[name]
	group, err := outboundgroup.ParseProxyGroup(local, b.proxies, b.providers, b.allProxies, b.allProviders)
	if err != nil {
		return nil, b.failure("invalid_group", name, err)
	}
	if before == nil {
		if closer, ok := b.providers[name].(io.Closer); ok {
			if err := b.own(closer); err != nil {
				return nil, err
			}
		}
	}
	if selected := b.selected[name]; selected != "" {
		if s, ok := group.(outboundgroup.SelectAble); ok {
			// Retain configured default when a persisted member has been filtered out.
			for _, candidate := range group.Proxies() {
				if candidate.Name() == selected {
					s.ForceSet(selected)
					break
				}
			}
		}
	}
	wrapped := &bootstrapGroup{ProxyGroup: group, owner: b}
	p := adapter.NewProxy(wrapped)
	b.proxies[name] = p
	return p, nil
}

func (b *bootstrapOutbound) root(name string, fromProvider bool) (C.Proxy, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if err := b.ctx.Err(); err != nil {
		return nil, resource.BootstrapFailure(b.ctx, err)
	}
	if err := b.initialize(); err != nil {
		return nil, err
	}
	p, err := b.lookup(name, fromProvider)
	if err != nil {
		return nil, err
	}
	if err := b.validate(p, make(map[C.Proxy]bool), make(map[C.Proxy]int)); err != nil {
		return nil, err
	}
	return p, nil
}

func (b *bootstrapOutbound) validate(p C.Proxy, path map[C.Proxy]bool, depths map[C.Proxy]int) error {
	if err := b.ctx.Err(); err != nil {
		return err
	}
	if path[p] {
		return b.failure("proxy_cycle", p.Name(), nil)
	}
	if depth := depths[p]; depth > 0 {
		// A shared subtree can be reached by a longer path later. Its cached
		// height must still count toward the same complete-chain bound.
		if len(path)+depth > 64 {
			return b.failure("proxy_chain_too_deep", p.Name(), nil)
		}
		return nil
	}
	if len(path) >= 64 {
		return b.failure("proxy_chain_too_deep", p.Name(), nil)
	}
	path[p] = true
	defer delete(path, p)
	depth := 1
	visit := func(child C.Proxy) error {
		if err := b.validate(child, path, depths); err != nil {
			return err
		}
		if childDepth := depths[child] + 1; childDepth > depth {
			depth = childDepth
		}
		return nil
	}
	if group, ok := p.Adapter().(*bootstrapGroup); ok {
		for _, child := range group.Proxies() {
			if err := visit(child); err != nil {
				return err
			}
		}
	} else {
		node, ok := p.Adapter().(*bootstrapNode)
		if !ok {
			return b.failure("invalid_proxy", p.Name(), nil)
		}
		kind, _ := node.mapping["type"].(string)
		if kind == "dns" || kind == "rematch" || kind == "pass" || kind == "pass-rule" || node.Type() < 0 {
			return b.failure("dynamic_proxy", p.Name(), nil)
		}
		if parent, _ := node.mapping["dialer-proxy"].(string); parent != "" {
			if kind == "direct" {
				return b.failure("invalid_proxy_chain", p.Name(), nil)
			}
			target, err := b.lookup(parent, false)
			if err != nil {
				return err
			}
			if err := visit(target); err != nil {
				return err
			}
		}
	}
	depths[p] = depth
	return nil
}

type bootstrapNode struct {
	*outbound.Base
	owner        *bootstrapOutbound
	mapping      map[string]any
	providerName string
	mu           sync.Mutex
	actual       C.ProxyAdapter
	err          error
}

func (n *bootstrapNode) adapter() (C.ProxyAdapter, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	if err := n.owner.ctx.Err(); err != nil {
		return nil, err
	}
	if n.actual != nil || n.err != nil {
		return n.actual, n.err
	}
	kind, _ := n.mapping["type"].(string)
	switch kind {
	case "direct", "compatible":
		option := outbound.DirectOption{}
		decoder := structure.NewDecoder(structure.Option{TagName: "proxy", WeaklyTypedInput: true, KeyReplacer: structure.DefaultKeyReplacer})
		if n.err = decoder.Decode(n.mapping, &option); n.err == nil {
			base := outbound.NewDirectWithOption(option)
			n.actual = &bootstrapDirect{Direct: base, sockets: n.owner.socketDialer(base.DialOptions())}
		}
	case "reject-drop":
		n.actual = outbound.NewRejectDrop()
	default:
		parent, _ := n.mapping["dialer-proxy"].(string)
		factory := func(opts []dialer.Option) C.Dialer {
			if parent != "" {
				return bootstrapNamedDialer{owner: n.owner, name: parent}
			}
			return n.owner.socketDialer(opts)
		}
		n.actual, n.err = adapter.ParseProxy(n.mapping, adapter.WithDialerFactoryForAPI(factory))
	}
	if n.err != nil {
		n.err = n.owner.failure("invalid_proxy", n.Name(), n.err)
		return nil, n.err
	}
	if err := n.owner.own(n.actual); err != nil {
		n.actual = nil
		return nil, err
	}
	return n.actual, nil
}

func (n *bootstrapNode) DialContext(ctx context.Context, metadata *C.Metadata) (C.Conn, error) {
	a, err := n.adapter()
	if err != nil {
		return nil, err
	}
	return a.DialContext(resolver.WithBootstrapResolver(icontext.WithDNSBootstrap(ctx)), metadata)
}
func (n *bootstrapNode) ListenPacketContext(ctx context.Context, metadata *C.Metadata) (C.PacketConn, error) {
	a, err := n.adapter()
	if err != nil {
		return nil, err
	}
	return a.ListenPacketContext(resolver.WithBootstrapResolver(icontext.WithDNSBootstrap(ctx)), metadata)
}

type bootstrapDirect struct {
	*outbound.Direct
	sockets C.Dialer
}

func (d *bootstrapDirect) DialContext(ctx context.Context, m *C.Metadata) (C.Conn, error) {
	conn, err := d.sockets.DialContext(ctx, "tcp", m.RemoteAddress())
	if err != nil {
		return nil, err
	}
	return outbound.NewConn(conn, d), nil
}
func (d *bootstrapDirect) ListenPacketContext(ctx context.Context, m *C.Metadata) (C.PacketConn, error) {
	if err := d.ResolveUDP(ctx, m); err != nil {
		return nil, err
	}
	conn, err := d.sockets.ListenPacket(ctx, "udp", "", m.AddrPort())
	if err != nil {
		return nil, err
	}
	return outbound.NewPacketConn(conn, d), nil
}

// Embed the normal group for filtering/selection only. Even nested URL probes
// and dialer-proxy chains dial through this wrapper, never the runtime group's
// onDialFailed goroutines or its Background-context health workers.
type bootstrapGroup struct {
	outboundgroup.ProxyGroup
	owner     *bootstrapOutbound
	probeMu   sync.Mutex
	probeDone bool
	probeWait chan struct{}
	preferred C.Proxy // successful complete GET, private fallback groups only
}

func (g *bootstrapGroup) DialContext(ctx context.Context, m *C.Metadata) (C.Conn, error) {
	conn, err := (bootstrapNamedDialer{owner: g.owner, name: g.Name()}).DialContext(ctx, "tcp", m.RemoteAddress())
	if err != nil {
		return nil, err
	}
	return outbound.NewConn(conn, g), nil
}
func (g *bootstrapGroup) ListenPacketContext(ctx context.Context, m *C.Metadata) (C.PacketConn, error) {
	request, err := g.owner.request(ctx, g.Name(), false, m.RemoteAddress())
	if err != nil {
		return nil, err
	}
	if _, err := request.Next(ctx); err != nil {
		return nil, err
	}
	return request.leaf.ListenPacketContext(ctx, m)
}
