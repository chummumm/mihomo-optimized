package adapter

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/netip"

	"github.com/metacubex/mihomo/adapter/outbound"
	"github.com/metacubex/mihomo/component/resolver"
	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"
)

type proxyTargetResolver interface {
	ResolveProxyTarget(context.Context, *C.Metadata) error
}

// proxyTargetAdapter is installed on a concrete protocol node, never a group.
// Its immutable configuration also belongs to existing UDP associations after
// reload. Only the protocol copy loses Host; rule/log metadata retains it.
type proxyTargetAdapter struct {
	outbound.ProxyAdapter
	targetResolver proxyTargetResolver
	preparer       C.ProxyAdapter
	family         C.TargetIPFamily
	prefer         C.DNSPrefer
}

func (a *proxyTargetAdapter) UnderlyingProxyAdapter() C.ProxyAdapter { return a.ProxyAdapter }

func (a *proxyTargetAdapter) PrepareLAN(ctx context.Context, target netip.Addr) error {
	if preparer, ok := a.preparer.(interface {
		PrepareLAN(context.Context, netip.Addr) error
	}); ok {
		return preparer.PrepareLAN(ctx, target)
	}
	return C.ErrNotSupport
}

func (a *proxyTargetAdapter) DNSStatsIdentity() string {
	if source, ok := a.ProxyAdapter.(interface{ DNSStatsIdentity() string }); ok {
		return source.DNSStatsIdentity()
	}
	return ""
}

func (a *proxyTargetAdapter) MarshalJSON() ([]byte, error) {
	data, err := a.ProxyAdapter.MarshalJSON()
	if err != nil {
		return nil, err
	}
	var fields map[string]any
	if err := json.Unmarshal(data, &fields); err != nil {
		return nil, err
	}
	fields["target-ip-family"] = a.family.String()
	fields["proxy-force-ip"] = true
	return json.Marshal(fields)
}

func (a *proxyTargetAdapter) target(ctx context.Context, original *C.Metadata) (*C.Metadata, []netip.Addr, error) {
	if err := ctx.Err(); err != nil {
		return nil, nil, err
	}
	m := original.Clone()
	m.TargetIPFamily = a.family
	m.TargetCandidates = nil
	if m.InboundDstIP.IsValid() && !m.DestinationOverride && m.DNSMode != C.DNSHosts {
		m.DstIP, m.Host = m.InboundDstIP.Unmap(), ""
	}
	if ip, err := netip.ParseAddr(m.Host); err == nil {
		m.DstIP, m.Host = ip.Unmap(), ""
	}
	if m.Host == "" {
		if resolver.IsFakeIP(m.DstIP) {
			return nil, nil, fmt.Errorf("proxy target is an unresolved fake-IP: %s", m.DstIP)
		}
		if err := a.family.Check(m.DstIP); err != nil {
			return nil, nil, err
		}
		m.DstIP = m.DstIP.Unmap()
		return m, []netip.Addr{m.DstIP}, nil
	}
	// DstIP may be a GEOIP/rule lookup in another resolver scope. A real
	// protocol-owned lookup below is the authority for a hostname target.
	m.DstIP = netip.Addr{}
	ctx = icontext.WithDNSFixedOutbound(icontext.WithDNSRoutingMetadata(ctx, original), a)
	if m.ProxyTransport || icontext.DNSBootstrap(ctx) {
		ctx = icontext.WithDNSBootstrap(ctx)
	}
	if a.targetResolver != nil {
		if err := a.targetResolver.ResolveProxyTarget(ctx, m); err != nil {
			return nil, nil, err
		}
		if resolver.IsFakeIP(m.DstIP) {
			return nil, nil, fmt.Errorf("proxy target is an unresolved fake-IP: %s", m.DstIP)
		}
		if err := a.family.Check(m.DstIP); err != nil {
			return nil, nil, err
		}
		m.Host = ""
		candidates := m.TargetCandidates
		if len(candidates) == 0 {
			candidates = []netip.Addr{m.DstIP}
		}
		return m, candidates, nil
	}
	r := resolver.DefaultResolver
	if m.ProxyTransport || icontext.DNSBootstrap(ctx) {
		r = resolver.ProxyServerHostResolver
		ctx = icontext.WithDNSBootstrap(ctx)
	}
	ips, err := outbound.LookupProxyTargets(ctx, m.Host, r, a.prefer, a.family)
	if err != nil {
		return nil, nil, err
	}
	m.Host, m.DstIP = "", ips[0]
	return m, ips, nil
}

func (a *proxyTargetAdapter) DialContext(ctx context.Context, metadata *C.Metadata) (C.Conn, error) {
	m, candidates, err := a.target(ctx, metadata)
	if err != nil {
		return nil, err
	}
	for _, ip := range candidates {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		if err := a.family.Check(ip); err != nil {
			return nil, err
		}
		if resolver.IsFakeIP(ip) {
			return nil, fmt.Errorf("proxy candidate is an unresolved fake-IP: %s", ip)
		}
		m.DstIP = ip
		conn, dialErr := a.ProxyAdapter.DialContext(ctx, m)
		if dialErr == nil {
			metadata.DstIP = m.DstIP
			return conn, nil
		}
		if conn != nil {
			_ = conn.Close()
		}
		err = dialErr
		// DialContext receives no application bytes. Once it returns a
		// connection, including a lazy/fast-open one, no candidate retry is
		// attempted here: a subsequent first Write may contain business data.
	}
	return nil, err
}

func (a *proxyTargetAdapter) ListenPacketContext(ctx context.Context, metadata *C.Metadata) (C.PacketConn, error) {
	m, _, err := a.target(ctx, metadata)
	if err != nil {
		return nil, err
	}
	pc, err := a.ProxyAdapter.ListenPacketContext(ctx, m)
	if err != nil {
		return nil, err
	}
	if err := a.family.Check(m.DstIP); err != nil {
		_ = pc.Close()
		return nil, err
	}
	metadata.DstIP = m.DstIP // The caller's NAT mapping must see the actual target.
	return &proxyTargetPacketConn{PacketConn: pc, adapter: a}, nil
}

func (a *proxyTargetAdapter) ResolveUDP(ctx context.Context, metadata *C.Metadata) error {
	m, _, err := a.target(ctx, metadata)
	if err != nil {
		return err
	}
	metadata.DstIP = m.DstIP
	return nil
}

type proxyTargetPacketConn struct {
	C.PacketConn
	adapter *proxyTargetAdapter
}

// ForceIPPolicy exposes the selected leaf's immutable policy to the packet
// sender through accounting wrappers. Later destinations need the same hosts
// and family selection as the first datagram, without selecting a group again.
func (pc *proxyTargetPacketConn) ForceIPPolicy() bool { return true }

func (pc *proxyTargetPacketConn) ResolveUDP(ctx context.Context, metadata *C.Metadata) error {
	// Use the live association/request context supplied by the packet sender,
	// never the already-cancelled initial ListenPacketContext context.
	return pc.adapter.ResolveUDP(ctx, metadata)
}

func (pc *proxyTargetPacketConn) WriteTo(data []byte, address net.Addr) (int, error) {
	if address == nil {
		return 0, fmt.Errorf("proxy UDP destination is missing")
	}
	target, err := netip.ParseAddrPort(address.String())
	if err != nil {
		return 0, fmt.Errorf("proxy UDP destination must be a resolved IP: %w", err)
	}
	if err := pc.adapter.family.Check(target.Addr()); err != nil {
		return 0, err
	}
	if resolver.IsFakeIP(target.Addr().Unmap()) {
		return 0, fmt.Errorf("proxy UDP destination is an unresolved fake-IP: %s", target.Addr())
	}
	return pc.PacketConn.WriteTo(data, address)
}

func proxyTargetExcluded(kind string) bool {
	switch kind {
	case "direct", "dns", "reject", "reject-drop", "compatible", "pass", "pass-rule", "rematch":
		return true
	}
	return false
}

func parseProxyTargetPolicy(mapping map[string]any, force bool) (C.TargetIPFamily, error) {
	value := "any"
	if raw, present := mapping["target-ip-family"]; present {
		var ok bool
		value, ok = raw.(string)
		if !ok || value == "" {
			return C.TargetIPAny, fmt.Errorf("target-ip-family must be any, ipv4 or ipv6")
		}
	}
	family, err := C.ParseTargetIPFamily(value)
	if err != nil {
		return family, err
	}
	if family != C.TargetIPAny {
		if !force {
			return family, fmt.Errorf("target-ip-family requires proxy-force-ip: true")
		}
		kind, _ := mapping["type"].(string)
		if proxyTargetExcluded(kind) {
			return family, fmt.Errorf("target-ip-family is not supported by %s", kind)
		}
		if kind == "easytier" && family == C.TargetIPv6 {
			return family, fmt.Errorf("EasyTier supports only IPv4 targets")
		}
	}
	return family, nil
}
