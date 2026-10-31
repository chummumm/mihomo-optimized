package adapter

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"strconv"
	"testing"
	"time"

	"github.com/metacubex/mihomo/adapter/outbound"
	"github.com/metacubex/mihomo/component/proxydialer"
	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/component/trie"
	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"
	"github.com/metacubex/mihomo/transport/socks5"
)

type targetTestResolver struct {
	resolver.Resolver
	ips   []netip.Addr
	err   error
	calls int
	seen  context.Context
	host  string
}

func (r *targetTestResolver) Invalid() bool { return true }
func (r *targetTestResolver) LookupIP(ctx context.Context, host string) ([]netip.Addr, error) {
	r.calls++
	r.seen, r.host = ctx, host
	return r.ips, r.err
}
func (r *targetTestResolver) LookupIPv4(ctx context.Context, host string) ([]netip.Addr, error) {
	ips, err := r.LookupIP(ctx, host)
	var result []netip.Addr
	for _, ip := range ips {
		if ip.Is4() {
			result = append(result, ip)
		}
	}
	return result, err
}
func (r *targetTestResolver) LookupIPv6(ctx context.Context, host string) ([]netip.Addr, error) {
	ips, err := r.LookupIP(ctx, host)
	var result []netip.Addr
	for _, ip := range ips {
		if ip.Is6() {
			result = append(result, ip)
		}
	}
	return result, err
}

func installTargetTestResolver(t *testing.T) *targetTestResolver {
	t.Helper()
	old, oldProxy, oldHosts, oldDisable := resolver.DefaultResolver, resolver.ProxyServerHostResolver, resolver.DefaultHosts, resolver.DisableIPv6
	r := &targetTestResolver{ips: []netip.Addr{netip.MustParseAddr("192.0.2.10"), netip.MustParseAddr("2001:db8::10")}}
	resolver.DefaultResolver, resolver.ProxyServerHostResolver, resolver.DisableIPv6 = r, r, false
	resolver.DefaultHosts = resolver.NewHosts(trie.New[resolver.HostValue]())
	t.Cleanup(func() {
		resolver.DefaultResolver, resolver.ProxyServerHostResolver, resolver.DefaultHosts, resolver.DisableIPv6 = old, oldProxy, oldHosts, oldDisable
	})
	return r
}

type targetTestProtocol struct {
	*outbound.Base
	seen      []*C.Metadata
	failFirst bool
}

func newTargetTestAdapter(family C.TargetIPFamily) (*proxyTargetAdapter, *targetTestProtocol) {
	node := &targetTestProtocol{Base: outbound.NewBase(outbound.BaseOption{Name: "leaf", Type: C.Socks5, UDP: true})}
	return &proxyTargetAdapter{ProxyAdapter: node, preparer: node, family: family}, node
}

func (p *targetTestProtocol) DialContext(_ context.Context, m *C.Metadata) (C.Conn, error) {
	p.seen = append(p.seen, m.Clone())
	if p.failFirst && len(p.seen) == 1 {
		return nil, errors.New("first address refused")
	}
	left, right := net.Pipe()
	_ = right.Close()
	return outbound.NewConn(left, p), nil
}
func (p *targetTestProtocol) ListenPacketContext(_ context.Context, m *C.Metadata) (C.PacketConn, error) {
	p.seen = append(p.seen, m.Clone())
	return &targetTestPacketConn{}, nil
}

type targetTestPacketConn struct{ C.PacketConn }

func (*targetTestPacketConn) Close() error                                 { return nil }
func (*targetTestPacketConn) WriteTo(data []byte, _ net.Addr) (int, error) { return len(data), nil }

func TestProxyTargetPreservesDestinationOrigin(t *testing.T) {
	for _, test := range []struct {
		name    string
		m       C.Metadata
		want    string
		lookups int
	}{
		{"domain", C.Metadata{Host: "site.example"}, "192.0.2.10", 1},
		{"rule lookup is not final", C.Metadata{Host: "site.example", DstIP: netip.MustParseAddr("192.0.2.99")}, "192.0.2.10", 1},
		{"literal", C.Metadata{DstIP: netip.MustParseAddr("192.0.2.11")}, "192.0.2.11", 0},
		{"IP mapping", C.Metadata{Host: "site.example", DstIP: netip.MustParseAddr("192.0.2.11"), InboundDstIP: netip.MustParseAddr("192.0.2.11"), DNSMode: C.DNSMapping}, "192.0.2.11", 0},
		{"sniff rule only", C.Metadata{SniffHost: "site.example", DstIP: netip.MustParseAddr("192.0.2.11"), InboundDstIP: netip.MustParseAddr("192.0.2.11")}, "192.0.2.11", 0},
		{"explicit override", C.Metadata{Host: "site.example", InboundDstIP: netip.MustParseAddr("192.0.2.11"), DestinationOverride: true}, "192.0.2.10", 1},
		{"hosts rewrite", C.Metadata{DstIP: netip.MustParseAddr("192.0.2.12"), InboundDstIP: netip.MustParseAddr("192.0.2.11"), DNSMode: C.DNSHosts}, "192.0.2.12", 0},
	} {
		t.Run(test.name, func(t *testing.T) {
			r := installTargetTestResolver(t)
			a, protocol := newTargetTestAdapter(C.TargetIPAny)
			test.m.DstPort = 443
			originalHost, originalSniff := test.m.Host, test.m.SniffHost
			conn, err := NewProxy(a, true).DialContext(context.Background(), &test.m)
			if err != nil {
				t.Fatal(err)
			}
			_ = conn.Close()
			if got := protocol.seen[0]; got.Host != "" || got.DstIP.String() != test.want || got.AddrType() == C.AtypDomainName {
				t.Fatalf("invalid protocol destination: %+v", got)
			}
			if test.m.Host != originalHost || test.m.SniffHost != originalSniff || test.m.DstIP.String() != test.want || r.calls != test.lookups {
				t.Fatalf("origin/result changed: %+v calls=%d", test.m, r.calls)
			}
		})
	}
}

func TestProxyTargetFamilyAndSafeCandidateRetry(t *testing.T) {
	r := installTargetTestResolver(t)
	a, protocol := newTargetTestAdapter(C.TargetIPv6)
	m := &C.Metadata{Host: "site.example", DstPort: 443}
	conn, err := a.DialContext(context.Background(), m)
	if err != nil {
		t.Fatal(err)
	}
	_ = conn.Close()
	if !protocol.seen[0].DstIP.Is6() {
		t.Fatal("IPv6 policy did not constrain candidates")
	}
	before := r.calls
	_, err = a.DialContext(context.Background(), &C.Metadata{DstIP: netip.MustParseAddr("192.0.2.10"), DstPort: 443})
	if !errors.Is(err, C.ErrTargetIPFamily) || r.calls != before {
		t.Fatalf("literal IP was re-resolved: %v", err)
	}
	r.ips = []netip.Addr{netip.MustParseAddr("192.0.2.10")}
	if _, err := a.DialContext(context.Background(), m); !errors.Is(err, resolver.ErrIPNotFound) {
		t.Fatalf("missing family did not fail: %v", err)
	}
	r.err = context.DeadlineExceeded
	if _, err := a.DialContext(context.Background(), m); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal("lost DNS timeout", err)
	}
	r.err = nil
	r.ips = []netip.Addr{netip.MustParseAddr("192.0.2.10"), netip.MustParseAddr("192.0.2.11")}
	a, protocol = newTargetTestAdapter(C.TargetIPAny)
	protocol.failFirst = true
	conn, err = a.DialContext(context.Background(), m)
	if err != nil {
		t.Fatal(err)
	}
	_ = conn.Close()
	if len(protocol.seen) != 2 || m.DstIP != r.ips[1] {
		t.Fatalf("candidate retry failed: %v %+v", protocol.seen, m)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := a.DialContext(ctx, m); !errors.Is(err, context.Canceled) {
		t.Fatal("ignored parent cancellation", err)
	}
}

func TestProxyTargetUDPUsesCurrentContextAndReturnsRealTarget(t *testing.T) {
	r := installTargetTestResolver(t)
	a, protocol := newTargetTestAdapter(C.TargetIPv4)
	initial := &C.Metadata{NetWork: C.UDP, Host: "first.example", DstPort: 53}
	ctx, cancel := context.WithCancel(context.Background())
	pc, err := NewProxy(a, true).ListenPacketContext(ctx, initial)
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	cancel()
	if initial.Host != "first.example" || !initial.DstIP.Is4() || protocol.seen[0].Host != "" {
		t.Fatalf("initial UDP target lost: %+v %+v", initial, protocol.seen[0])
	}
	next := &C.Metadata{NetWork: C.UDP, Host: "second.example", DstPort: 53, SrcIP: netip.MustParseAddr("192.168.1.25"), InName: "second-source"}
	if err := pc.ResolveUDP(context.Background(), next); err != nil {
		t.Fatal("captured expired initial context", err)
	}
	if next.Host != "second.example" || !next.DstIP.Is4() || r.host != "second.example" || icontext.DNSFixedOutbound(r.seen) != a {
		t.Fatal("new UDP target did not use actual leaf")
	}
	if origin := icontext.DNSRoutingMetadata(r.seen); origin.InName != next.InName || origin.SrcIP != next.SrcIP {
		t.Fatalf("lost packet source: %+v", origin)
	}
	if err := pc.ResolveUDP(context.Background(), &C.Metadata{DstIP: netip.MustParseAddr("2001:db8::1")}); !errors.Is(err, C.ErrTargetIPFamily) {
		t.Fatal("later numeric UDP target bypassed family", err)
	}
	if _, err := pc.WriteTo([]byte("once"), net.UDPAddrFromAddrPort(netip.MustParseAddrPort("[2001:db8::1]:53"))); !errors.Is(err, C.ErrTargetIPFamily) {
		t.Fatal("direct UDP WriteTo bypassed family", err)
	}
}

type privateTargetProtocol struct {
	*targetTestProtocol
	calls int
}

func (p *privateTargetProtocol) ResolveProxyTarget(_ context.Context, m *C.Metadata) error {
	p.calls++
	m.DstIP = netip.MustParseAddr("192.168.5.7")
	return m.TargetIPFamily.Check(m.DstIP)
}

func TestProxyTargetRetainsProtocolPrivateResolverAndBootstrap(t *testing.T) {
	r := installTargetTestResolver(t)
	a, protocol := newTargetTestAdapter(C.TargetIPv4)
	private := &privateTargetProtocol{targetTestProtocol: protocol}
	a.ProxyAdapter, a.targetResolver = private, private
	conn, err := a.DialContext(context.Background(), &C.Metadata{Host: "private.example", DstPort: 443})
	if err != nil {
		t.Fatal(err)
	}
	_ = conn.Close()
	if r.calls != 0 || private.calls != 1 || protocol.seen[0].DstIP.String() != "192.168.5.7" {
		t.Fatal("replaced protocol-private DNS")
	}
	a, _ = newTargetTestAdapter(C.TargetIPv4)
	conn, err = a.DialContext(context.Background(), &C.Metadata{Host: "next-hop.example", DstPort: 443, ProxyTransport: true})
	if err != nil {
		t.Fatal(err)
	}
	_ = conn.Close()
	if !icontext.DNSBootstrap(r.seen) || icontext.DNSFixedOutbound(r.seen) != nil {
		t.Fatal("next-hop resolution inherited business DNS routing")
	}
}

func TestProxyTargetConfigurationAndDisabledBehavior(t *testing.T) {
	for _, test := range []struct {
		kind, family   string
		force, wantErr bool
	}{
		{"socks5", "any", false, false}, {"socks5", "ipv4", false, true}, {"socks5", "ipv4", true, false},
		{"socks5", "auto", true, true}, {"direct", "ipv4", true, true}, {"easytier", "ipv6", true, true},
	} {
		_, err := parseProxyTargetPolicy(map[string]any{"type": test.kind, "target-ip-family": test.family}, test.force)
		if (err != nil) != test.wantErr {
			t.Fatalf("%+v: %v", test, err)
		}
	}
	for _, force := range []bool{false, true} {
		p, err := ParseProxy(map[string]any{"type": "socks5", "name": "test", "server": "127.0.0.1", "port": 1080}, WithProxyForceIP(force))
		if err != nil {
			t.Fatal(err)
		}
		_, wrapped := p.Adapter().(*proxyTargetAdapter)
		if wrapped != force {
			t.Fatalf("wrong default/explicit behavior: %v", wrapped)
		}
		_ = p.Close()
		direct, err := ParseProxy(map[string]any{"type": "direct", "name": "direct"}, WithProxyForceIP(force))
		if err != nil {
			t.Fatal(err)
		}
		if _, wrapped := direct.Adapter().(*proxyTargetAdapter); wrapped {
			t.Fatal("DIRECT was changed")
		}
		_ = direct.Close()
	}
}

// Observe protocol bytes, rather than merely asserting DstIP on an internal
// metadata copy. The remote proxy sees a numeric CONNECT destination.
func TestProxyTargetSOCKSAndHTTPWireDestination(t *testing.T) {
	for _, kind := range []string{"socks5", "http"} {
		for _, family := range []C.TargetIPFamily{C.TargetIPv4, C.TargetIPv6} {
			t.Run(kind+"/"+family.String(), func(t *testing.T) {
				installTargetTestResolver(t)
				listener, err := net.Listen("tcp4", "127.0.0.1:0")
				if err != nil {
					t.Fatal(err)
				}
				defer listener.Close()
				observed := make(chan string, 1)
				serverErrors := make(chan error, 1)
				go func() {
					conn, err := listener.Accept()
					if err != nil {
						serverErrors <- err
						return
					}
					defer conn.Close()
					_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
					if kind == "http" {
						request, err := http.ReadRequest(bufio.NewReader(conn))
						if err != nil {
							serverErrors <- err
							return
						}
						observed <- request.Host
						_, err = io.WriteString(conn, "HTTP/1.1 200 Connection established\r\n\r\n")
						serverErrors <- err
						return
					}
					header := make([]byte, 2)
					if _, err := io.ReadFull(conn, header); err != nil {
						serverErrors <- err
						return
					}
					methods := make([]byte, int(header[1]))
					_, err = io.ReadFull(conn, methods)
					if err != nil {
						serverErrors <- err
						return
					}
					if _, err = conn.Write([]byte{5, 0}); err != nil {
						serverErrors <- err
						return
					}
					header = make([]byte, 4)
					if _, err = io.ReadFull(conn, header); err != nil {
						serverErrors <- err
						return
					}
					size := 4
					if header[3] == 4 {
						size = 16
					} else if header[3] != 1 {
						serverErrors <- fmt.Errorf("non-numeric SOCKS ATYP: %d", header[3])
						return
					}
					address := make([]byte, size+2)
					if _, err = io.ReadFull(conn, address); err != nil {
						serverErrors <- err
						return
					}
					ip, _ := netip.AddrFromSlice(address[:size])
					observed <- net.JoinHostPort(ip.String(), "443")
					_, err = conn.Write([]byte{5, 0, 0, 1, 127, 0, 0, 1, 0, 1})
					serverErrors <- err
				}()
				port := listener.Addr().(*net.TCPAddr).Port
				p, err := ParseProxy(map[string]any{"type": kind, "name": "wire-test", "server": "127.0.0.1", "port": port, "target-ip-family": family.String()}, WithProxyForceIP(true))
				if err != nil {
					t.Fatal(err)
				}
				defer p.Close()
				ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
				defer cancel()
				m := &C.Metadata{Host: "site.example", DstPort: 443, NetWork: C.TCP}
				conn, err := p.DialContext(ctx, m)
				if err != nil {
					t.Fatal(err)
				}
				_ = conn.Close()
				if err := <-serverErrors; err != nil {
					t.Fatal(err)
				}
				got := <-observed
				want := net.JoinHostPort(m.DstIP.String(), strconv.Itoa(int(m.DstPort)))
				if got != want || m.Host != "site.example" {
					t.Fatalf("wire=%s, want=%s, metadata=%+v", got, want, m)
				}
			})
		}
	}
}

func TestProxyTargetSOCKSUDPWireAndReplies(t *testing.T) {
	for _, family := range []C.TargetIPFamily{C.TargetIPv4, C.TargetIPv6} {
		t.Run(family.String(), func(t *testing.T) {
			r := installTargetTestResolver(t)
			listener, err := net.Listen("tcp4", "127.0.0.1:0")
			if err != nil {
				t.Fatal(err)
			}
			defer listener.Close()
			port := listener.Addr().(*net.TCPAddr).Port
			relay, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: port})
			if err != nil {
				t.Fatal(err)
			}
			defer relay.Close()
			serverDone := make(chan error, 1)
			go func() {
				conn, err := listener.Accept()
				if err != nil {
					serverDone <- err
					return
				}
				defer conn.Close()
				_ = conn.SetDeadline(time.Now().Add(5 * time.Second))
				_, command, _, err := socks5.ServerHandshake(conn, nil)
				if err == nil && command != socks5.CmdUDPAssociate {
					err = fmt.Errorf("unexpected command %d", command)
				}
				if err == nil {
					_, err = io.Copy(io.Discard, conn)
				}
				serverDone <- err
			}()
			p, err := ParseProxy(map[string]any{"type": "socks5", "name": "udp-wire", "server": "127.0.0.1", "port": port, "udp": true, "target-ip-family": family.String()}, WithProxyForceIP(true))
			if err != nil {
				t.Fatal(err)
			}
			defer p.Close()
			initial := &C.Metadata{NetWork: C.UDP, Host: "first.example", DstPort: 5353}
			ctx, cancel := context.WithCancel(context.Background())
			pc, err := p.ListenPacketContext(ctx, initial)
			cancel()
			if err != nil {
				t.Fatal(err)
			}
			defer func() { _ = pc.Close(); <-serverDone }()
			for i, m := range []*C.Metadata{initial, {NetWork: C.UDP, Host: "second.example", DstPort: 5354}} {
				if i != 0 {
					r.ips = []netip.Addr{netip.MustParseAddr("192.0.2.20"), netip.MustParseAddr("2001:db8::20")}
					if err := pc.ResolveUDP(context.Background(), m); err != nil {
						t.Fatal(err)
					}
				}
				payload := []byte(fmt.Sprintf("request-%d", i))
				if _, err := pc.WriteTo(payload, m.UDPAddr()); err != nil {
					t.Fatal(err)
				}
				_ = relay.SetReadDeadline(time.Now().Add(2 * time.Second))
				buffer := make([]byte, 1024)
				n, from, err := relay.ReadFromUDP(buffer)
				if err != nil {
					t.Fatal(err)
				}
				destination, data, err := socks5.DecodeUDPPacket(buffer[:n])
				if err != nil || destination[0] == socks5.AtypDomainName || destination.String() != m.UDPAddr().String() || string(data) != string(payload) {
					t.Fatalf("bad UDP wire target %v %q: %v", destination, data, err)
				}
				if _, err := relay.WriteToUDP(buffer[:n], from); err != nil {
					t.Fatal(err)
				}
				_ = pc.SetReadDeadline(time.Now().Add(2 * time.Second))
				n, reply, err := pc.ReadFrom(buffer)
				if err != nil || reply.String() != m.UDPAddr().String() || string(buffer[:n]) != string(payload) {
					t.Fatalf("reply lost actual target: %v %v", reply, err)
				}
			}
		})
	}
}

type chainTargetProtocol struct {
	*targetTestProtocol
	transport C.Dialer
}

func (p *chainTargetProtocol) DialContext(ctx context.Context, m *C.Metadata) (C.Conn, error) {
	p.seen = append(p.seen, m.Clone())
	conn, err := p.transport.DialContext(ctx, "tcp", "proxy-a.example:443")
	if err != nil {
		return nil, err
	}
	return outbound.NewConn(conn, p), nil
}

func TestProxyTargetFamilyIsPerHopNotChainIntersection(t *testing.T) {
	businessDNS := installTargetTestResolver(t)
	businessDNS.ips = []netip.Addr{netip.MustParseAddr("2001:db8::99")}
	bootstrap := &targetTestResolver{ips: []netip.Addr{netip.MustParseAddr("192.0.2.99")}}
	resolver.ProxyServerHostResolver = bootstrap
	inner, innerProtocol := newTargetTestAdapter(C.TargetIPv4)
	outer, outerProtocol := newTargetTestAdapter(C.TargetIPv6)
	chain := &chainTargetProtocol{targetTestProtocol: outerProtocol, transport: proxydialer.New(NewProxy(inner, true), false)}
	outer.ProxyAdapter = chain
	metadata := &C.Metadata{Host: "business.example", DstPort: 443}
	conn, err := NewProxy(outer, true).DialContext(context.Background(), metadata)
	if err != nil {
		t.Fatal(err)
	}
	_ = conn.Close()
	if outerProtocol.seen[0].DstIP != businessDNS.ips[0] || innerProtocol.seen[0].DstIP != bootstrap.ips[0] {
		t.Fatal("intersected destination and carrier families")
	}
	if businessDNS.calls != 1 || bootstrap.calls != 1 || !icontext.DNSBootstrap(bootstrap.seen) {
		t.Fatal("next-hop DNS did not retain bootstrap scope")
	}
	// The carrier must still enforce its own next-hop family. Changing A's
	// entry to IPv6 cannot bypass B's IPv4 policy or submit a carrier dial.
	bootstrap.ips = []netip.Addr{netip.MustParseAddr("2001:db8::101")}
	innerProtocol.seen = nil
	conn, err = NewProxy(outer, true).DialContext(context.Background(), &C.Metadata{Host: "business.example", DstPort: 443})
	if conn != nil || !errors.Is(err, C.ErrNoProxyTarget) || len(innerProtocol.seen) != 0 {
		t.Fatalf("carrier family was bypassed: conn=%v error=%v attempts=%d", conn, err, len(innerProtocol.seen))
	}
}

func TestProxyTargetUDPHostsRewriteChoosesPermittedFamily(t *testing.T) {
	r := installTargetTestResolver(t)
	v4, v6 := netip.MustParseAddr("203.0.113.20"), netip.MustParseAddr("2001:db8::20")
	hosts := trie.New[resolver.HostValue]()
	if err := hosts.Insert("rewrite.example", resolver.HostValue{IPs: []netip.Addr{v6, v4}}); err != nil {
		t.Fatal(err)
	}
	resolver.DefaultHosts = resolver.NewHosts(hosts)
	a, _ := newTargetTestAdapter(C.TargetIPv4)
	pc, err := a.ListenPacketContext(context.Background(), &C.Metadata{DstIP: v4, DstPort: 443, NetWork: C.UDP})
	if err != nil {
		t.Fatal(err)
	}
	defer pc.Close()
	m := &C.Metadata{Host: "rewrite.example", DstIP: v6, InboundDstIP: netip.MustParseAddr("192.0.2.20"),
		DstPort: 443, NetWork: C.UDP, DNSMode: C.DNSMapping, DestinationOverride: true}
	if err := pc.ResolveUDP(context.Background(), m); err != nil {
		t.Fatal(err)
	}
	if m.DstIP != v4 || m.Host != "rewrite.example" || r.calls != 0 {
		t.Fatalf("hosts family decision changed: target=%v DNS calls=%d", m, r.calls)
	}
	if policy, ok := pc.(interface{ ForceIPPolicy() bool }); !ok || !policy.ForceIPPolicy() {
		t.Fatal("established connection lost its target policy snapshot")
	}
}
