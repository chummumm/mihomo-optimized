package adapter

import (
	"context"
	"errors"
	"net/netip"
	"testing"

	"github.com/metacubex/mihomo/adapter/outbound"
	"github.com/metacubex/mihomo/component/resolver"
	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"
	"github.com/metacubex/mihomo/tunnel"
)

var errDNSDialerTargetReached = errors.New("DNS upstream reached the protocol without network I/O")

// A normal non-L3 protocol receives the final force-IP destination, then stops
// before any network I/O. The actual DNSDialer and force-IP adapter remain in
// the call chain, which is where an explicit/Global upstream lost its resolver.
type dnsDialerTargetProtocol struct {
	*outbound.Base
	seen []*C.Metadata
}

func (p *dnsDialerTargetProtocol) DialContext(_ context.Context, m *C.Metadata) (C.Conn, error) {
	p.seen = append(p.seen, m.Clone())
	return nil, errDNSDialerTargetReached
}

func (p *dnsDialerTargetProtocol) ListenPacketContext(_ context.Context, m *C.Metadata) (C.PacketConn, error) {
	p.seen = append(p.seen, m.Clone())
	return nil, errDNSDialerTargetReached
}

func newDNSDialerTargetProxy(force bool, family C.TargetIPFamily) (*Proxy, *dnsDialerTargetProtocol) {
	protocol := &dnsDialerTargetProtocol{Base: outbound.NewBase(outbound.BaseOption{Name: "dns-leaf", Type: C.Socks5, UDP: true})}
	if !force {
		return NewProxy(protocol), protocol
	}
	a := &proxyTargetAdapter{ProxyAdapter: protocol, preparer: protocol, family: family}
	return NewProxy(a, true), protocol
}

func installDNSDialerTargetResolvers(t *testing.T) (business, node, bootstrap *targetTestResolver) {
	t.Helper()
	business = installTargetTestResolver(t)
	business.ips, business.err = nil, errors.New("unexpected business DNS lookup")
	node = &targetTestResolver{err: errors.New("unexpected proxy node DNS lookup")}
	resolver.ProxyServerHostResolver = node
	bootstrap = &targetTestResolver{ips: []netip.Addr{netip.MustParseAddr("192.0.2.53"), netip.MustParseAddr("2001:db8::53")}}
	return
}

func dnsDialerTargetEntry(t *testing.T, entry string, bootstrap resolver.Resolver, p *Proxy) *tunnel.DNSDialer {
	t.Helper()
	if entry == "adapter" {
		return tunnel.NewDNSDialer(bootstrap, p, "")
	}
	oldMode, oldProxies, oldProviders := tunnel.Mode(), tunnel.Proxies(), tunnel.Providers()
	t.Cleanup(func() {
		tunnel.UpdateProxies(oldProxies, oldProviders)
		tunnel.SetMode(oldMode)
	})
	if entry == "named" {
		tunnel.UpdateProxies(map[string]C.Proxy{"dns-exit": p}, nil)
		tunnel.SetMode(tunnel.Rule)
		return tunnel.NewDNSDialer(bootstrap, nil, "dns-exit")
	}
	if entry != "global-rules" {
		t.Fatalf("unknown DNS dialer entry %q", entry)
	}
	tunnel.UpdateProxies(map[string]C.Proxy{"GLOBAL": p}, nil)
	tunnel.SetMode(tunnel.Global)
	return tunnel.NewDNSDialer(bootstrap, nil, tunnel.DnsRespectRules)
}

func callDNSDialerTarget(d *tunnel.DNSDialer, ctx context.Context, operation string) error {
	const endpoint = "resolver.example:443"
	if operation == "packet" {
		pc, err := d.ListenPacket(ctx, "udp", endpoint)
		if pc != nil {
			_ = pc.Close()
		}
		return err
	}
	conn, err := d.DialContext(ctx, operation, endpoint)
	if conn != nil {
		_ = conn.Close()
	}
	return err
}

func TestDNSDialerProxyTargetKeepsUpstreamBootstrap(t *testing.T) {
	for _, entry := range []string{"adapter", "named", "global-rules"} {
		for _, operation := range []string{"tcp", "udp", "packet"} {
			for _, family := range []C.TargetIPFamily{C.TargetIPv4, C.TargetIPv6} {
				t.Run(entry+"/"+operation+"/"+family.String(), func(t *testing.T) {
					business, node, bootstrap := installDNSDialerTargetResolvers(t)
					p, protocol := newDNSDialerTargetProxy(true, family)
					d := dnsDialerTargetEntry(t, entry, bootstrap, p)
					if err := callDNSDialerTarget(d, context.Background(), operation); !errors.Is(err, errDNSDialerTargetReached) {
						t.Fatalf("DNS upstream never reached its protocol: %v", err)
					}
					if business.calls != 0 || node.calls != 0 || bootstrap.calls == 0 {
						t.Fatalf("wrong resolver: business=%d node=%d bootstrap=%d", business.calls, node.calls, bootstrap.calls)
					}
					if bootstrap.host != "resolver.example" || !icontext.DNSBootstrap(bootstrap.seen) || icontext.DNSFixedOutbound(bootstrap.seen) != nil {
						t.Fatal("force-IP bootstrap inherited business routing or the wrong hostname")
					}
					if len(protocol.seen) != 1 {
						t.Fatalf("unexpected protocol attempts: %d", len(protocol.seen))
					}
					m := protocol.seen[0]
					want := bootstrap.ips[0]
					if family == C.TargetIPv6 {
						want = bootstrap.ips[1]
					}
					if m.Host != "" || m.DstIP != want || m.DstPort != 443 || m.AddrType() == C.AtypDomainName {
						t.Fatalf("wrong numeric DNS target: %+v, want %s", m, want)
					}
					wantNetwork := C.UDP
					if operation == "tcp" {
						wantNetwork = C.TCP
					}
					if m.NetWork != wantNetwork {
						t.Fatalf("DNS transport changed network: %s", m.NetWork)
					}
				})
			}
		}
	}
}

func TestDNSDialerProxyTargetDisabledRetainsLegacyDomainTarget(t *testing.T) {
	for _, operation := range []string{"tcp", "udp", "packet"} {
		t.Run(operation, func(t *testing.T) {
			business, node, bootstrap := installDNSDialerTargetResolvers(t)
			p, protocol := newDNSDialerTargetProxy(false, C.TargetIPAny)
			d := tunnel.NewDNSDialer(bootstrap, p, "")
			if err := callDNSDialerTarget(d, context.Background(), operation); !errors.Is(err, errDNSDialerTargetReached) {
				t.Fatal(err)
			}
			if len(protocol.seen) != 1 || protocol.seen[0].Host != "resolver.example" {
				t.Fatalf("disabled force-IP changed the legacy target: %+v", protocol.seen)
			}
			wantLookups := 1
			if operation == "tcp" {
				wantLookups = 0
			}
			if bootstrap.calls != wantLookups || business.calls != 0 || node.calls != 0 {
				t.Fatalf("disabled force-IP changed DNS lookup behavior: bootstrap=%d business=%d node=%d", bootstrap.calls, business.calls, node.calls)
			}
		})
	}
}

func TestDNSDialerProxyTargetExplicitNilUsesSystemResolver(t *testing.T) {
	business, node, system := installDNSDialerTargetResolvers(t)
	oldSystem := resolver.SystemResolver
	resolver.SystemResolver = system
	t.Cleanup(func() { resolver.SystemResolver = oldSystem })
	p, protocol := newDNSDialerTargetProxy(true, C.TargetIPv4)
	d := tunnel.NewDNSDialer(nil, p, "")
	if err := callDNSDialerTarget(d, context.Background(), "tcp"); !errors.Is(err, errDNSDialerTargetReached) {
		t.Fatal(err)
	}
	if system.calls != 1 || business.calls != 0 || node.calls != 0 {
		t.Fatalf("nil upstream resolver fell back to business DNS: system=%d business=%d node=%d", system.calls, business.calls, node.calls)
	}
	if len(protocol.seen) != 1 || protocol.seen[0].DstIP != system.ips[0] {
		t.Fatalf("system bootstrap target was lost: %+v", protocol.seen)
	}
}

func TestDNSDialerProxyTargetBootstrapFailureDoesNotUseBusinessDNS(t *testing.T) {
	for _, operation := range []string{"tcp", "udp", "packet"} {
		for _, cancelFirst := range []bool{false, true} {
			name := operation + "/lookup-error"
			if cancelFirst {
				name = operation + "/cancelled"
			}
			t.Run(name, func(t *testing.T) {
				business, node, bootstrap := installDNSDialerTargetResolvers(t)
				lookupErr := errors.New("dedicated bootstrap failed")
				bootstrap.ips, bootstrap.err = nil, lookupErr
				p, protocol := newDNSDialerTargetProxy(true, C.TargetIPv4)
				d := tunnel.NewDNSDialer(bootstrap, p, "")
				ctx, cancel := context.WithCancel(context.Background())
				defer cancel()
				wantErr := lookupErr
				if cancelFirst {
					cancel()
					bootstrap.err = context.Canceled
					wantErr = context.Canceled
				}
				if err := callDNSDialerTarget(d, ctx, operation); !errors.Is(err, wantErr) {
					t.Fatalf("bootstrap failure changed: got %v, want %v", err, wantErr)
				}
				if len(protocol.seen) != 0 || business.calls != 0 || node.calls != 0 {
					t.Fatalf("failed bootstrap escaped into another resolver or dial: protocol=%d business=%d node=%d", len(protocol.seen), business.calls, node.calls)
				}
			})
		}
	}
}

func TestProxyTargetDNSUpstreamScopeDoesNotReplaceSameHostnameProxyHopResolver(t *testing.T) {
	business, node, bootstrap := installDNSDialerTargetResolvers(t)
	node.ips, node.err = []netip.Addr{netip.MustParseAddr("192.0.2.99")}, nil
	p, protocol := newDNSDialerTargetProxy(true, C.TargetIPv4)
	ctx := resolver.WithDNSUpstreamResolver(context.Background(), "resolver.example", bootstrap)
	_, err := p.DialContext(ctx, &C.Metadata{Host: "resolver.example", DstPort: 8443, NetWork: C.TCP, ProxyTransport: true})
	if !errors.Is(err, errDNSDialerTargetReached) {
		t.Fatal(err)
	}
	if bootstrap.calls != 0 || business.calls != 0 || node.calls != 1 || !icontext.DNSBootstrap(node.seen) {
		t.Fatalf("proxy hop borrowed the DNS upstream resolver: bootstrap=%d business=%d node=%d", bootstrap.calls, business.calls, node.calls)
	}
	if len(protocol.seen) != 1 || protocol.seen[0].DstIP != node.ips[0] {
		t.Fatalf("proxy hop used the resolver destination: %+v", protocol.seen)
	}
}

func TestDNSDialerProxyTargetUpstreamScopeKeepsProtocolPrivateResolver(t *testing.T) {
	business, node, bootstrap := installDNSDialerTargetResolvers(t)
	a, protocol := newTargetTestAdapter(C.TargetIPv4)
	private := &privateTargetProtocol{targetTestProtocol: protocol}
	a.ProxyAdapter, a.targetResolver = private, private
	d := tunnel.NewDNSDialer(bootstrap, NewProxy(a, true), "")
	if err := callDNSDialerTarget(d, context.Background(), "tcp"); err != nil {
		t.Fatal(err)
	}
	if private.calls != 1 || bootstrap.calls != 0 || business.calls != 0 || node.calls != 0 {
		t.Fatal("upstream scope replaced the protocol-private resolver")
	}
	if len(protocol.seen) != 1 || protocol.seen[0].DstIP.String() != "192.168.5.7" {
		t.Fatalf("private DNS target changed: %+v", protocol.seen)
	}
}
