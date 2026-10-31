//go:build !no_easytier

package outbound

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"syscall"
	"testing"
	"time"

	"github.com/metacubex/mihomo/component/easytier"
	"github.com/metacubex/mihomo/component/lanpreconnect"
	C "github.com/metacubex/mihomo/constant"

	corehost "github.com/easytier/easytier/easytier-go"
	"github.com/easytier/easytier/easytier-go/platform"
	"github.com/easytier/easytier/easytier-go/platform/netstd"
)

// This exercises the embedded WASM and real loopback sockets on Linux/Windows.
// CI opts in explicitly; ordinary unit tests need neither sockets nor a WASM
// compilation. No peer, STUN server, DNS name or destination uses the Internet.
func TestEasyTierFirstDialWaitsForDelayedRelayRoute(t *testing.T) {
	testEasyTierBackgroundRelay(t, false)
}

func TestLANEasyTierSubnetPreparesBeforeBusiness(t *testing.T) {
	testEasyTierBackgroundRelay(t, true)
}

func testEasyTierBackgroundRelay(t *testing.T, subnet bool) {
	if os.Getenv("MIHOMO_EASYTIER_INTEGRATION") != "1" {
		t.Skip("set MIHOMO_EASYTIER_INTEGRATION=1 for the real three-node relay regression")
	}

	const routeDelay = 6 * time.Second // Background startup may exceed a business dial budget.
	const network = "mihomo-first-dial-integration"
	relayIP := netip.MustParseAddr("10.78.0.1")
	targetIP := netip.MustParseAddr("10.78.0.2")
	businessIP := targetIP
	if subnet {
		businessIP = netip.MustParseAddr("192.168.42.7")
	}
	clientIP := netip.MustParseAddr("10.78.0.3")
	stateRoot := t.TempDir()
	originalHome := C.Path.HomeDir()
	C.SetHomeDir(stateRoot)
	t.Cleanup(func() { C.SetHomeDir(originalHome) })
	// Leave five seconds for bounded cleanup, including on a failed assertion.
	ctx, cancel := context.WithTimeout(context.Background(), 55*time.Second)
	var host *corehost.Host
	var relay, target *corehost.Instance
	var client *EasyTier
	var listener net.Listener
	var connection net.Conn
	t.Cleanup(func() {
		cancel()
		if connection != nil {
			_ = connection.Close()
		}
		if listener != nil {
			_ = listener.Close()
		}
		cleanup, stop := context.WithTimeout(context.Background(), 5*time.Second)
		defer stop()
		closed := make(chan struct{})
		go func() {
			if client != nil {
				_ = client.Close()
			}
			close(closed)
		}()
		if target != nil {
			_ = target.Close(cleanup)
		}
		if relay != nil {
			_ = relay.Close(cleanup)
		}
		if host != nil {
			_ = host.Close(cleanup)
		}
		select {
		case <-closed:
		case <-cleanup.Done():
			t.Error("EasyTier client cleanup exceeded five seconds")
		}
	})

	// The target's game listener is ready before its underlay connection is
	// admitted. This isolates missing overlay routes from an unready server.
	admitTarget := make(chan struct{})
	sockets := &easyTierRelaySockets{
		admit: admitTarget, listening: make(chan int, 1), attempted: make(chan struct{}),
	}
	var err error
	if subnet {
		// The remote gateway's native ProxyNAT socket targets this LAN host.
		// Only the final host socket is mapped to loopback by the fixture; the
		// real three-node overlay, proxy-CIDR route and NAT remain in the core.
		listener, err = net.Listen("tcp4", "127.0.0.1:0")
		if err != nil {
			t.Fatal(err)
		}
		sockets.lanTarget = netip.AddrPortFrom(businessIP, uint16(listener.Addr().(*net.TCPAddr).Port))
		sockets.lanLoopback = listener.Addr().(*net.TCPAddr)
	}
	host, err = corehost.New(ctx, corehost.Options{Platform: platform.Services{Sockets: sockets}})
	if err != nil {
		t.Fatalf("create relay/target host: %v", err)
	}
	build := func(name string, address netip.Addr) *corehost.InstanceConfigBuilder {
		return corehost.NewInstanceConfigBuilder(network).
			NetworkSecret("local-test-only").Hostname(name).
			IPv4(netip.PrefixFrom(address, 24)).
			STUNServers().STUNServersV6().
			P2P(corehost.P2PPolicy{Disable: true}).
			HolePunching(corehost.HolePunchingPolicy{}).Encryption(false)
	}
	create := func(builder *corehost.InstanceConfigBuilder) *corehost.Instance {
		t.Helper()
		config, configErr := builder.Build()
		if configErr != nil {
			t.Fatalf("build local EasyTier configuration: %v", configErr)
		}
		instance, createErr := host.CreateInstance(ctx, config)
		if createErr != nil {
			t.Fatalf("create local EasyTier instance: %v", createErr)
		}
		return instance
	}
	relay = create(build("local-relay", relayIP).AddListeners("tcp://127.0.0.1:0"))
	if err = relay.Start(ctx); err != nil {
		t.Fatalf("start relay: %v", err)
	}
	var relayPort int
	select {
	case relayPort = <-sockets.listening:
	case <-ctx.Done():
		t.Fatalf("relay did not open its loopback listener: %v", ctx.Err())
	}
	relayURI := fmt.Sprintf("tcp://127.0.0.1:%d", relayPort)
	if subnet {
		disabled, enabled := false, true
		config, configErr := (easytier.Config{
			NetworkName: network, NetworkSecret: "local-test-only", Hostname: "local-target",
			IPv4: targetIP.String() + "/24", Peers: []string{relayURI}, ProxyNetworks: []string{"192.168.42.0/24"},
			NoListener: &enabled, DisableP2P: &enabled, EnableEncryption: &disabled,
		}).RenderTOML()
		if configErr != nil {
			t.Fatal(configErr)
		}
		target, err = host.CreateInstanceTOML(ctx, "subnet-gateway", "", "stun_servers = []\nstun_servers_v6 = []\n"+config+"use_smoltcp = true\n")
		if err != nil {
			t.Fatal(err)
		}
	} else {
		target = create(build("local-target", targetIP).AddPeers(relayURI))
	}
	if err = target.Start(ctx); err != nil {
		t.Fatalf("start target: %v", err)
	}
	if !subnet {
		listener, err = easyTierListenTCP(ctx, target)
		if err != nil {
			t.Fatalf("prepare target TCP service: %v", err)
		}
	}
	select {
	case <-sockets.attempted:
	case <-ctx.Done():
		t.Fatalf("target did not attempt its gated relay connection: %v", ctx.Err())
	}
	relayInfo, err := relay.ShowNodeInfo(ctx)
	if err != nil || relayInfo.GetPeerId() == 0 {
		t.Fatalf("read relay peer ID: info=%v error=%v", relayInfo, err)
	}

	disabled, enabled := false, true
	client, err = NewEasyTier(EasyTierOption{
		Name: "first-dial-integration", NetworkName: network, NetworkSecret: "local-test-only",
		Hostname: "local-client", IPv4: clientIP.String() + "/24", Peers: []string{relayURI},
		NoListener: &enabled, DisableP2P: &enabled, EnableEncryption: &disabled,
		StateDir: filepath.Join(stateRoot, "client"),
	})
	if err != nil {
		t.Fatalf("create Mihomo EasyTier outbound: %v", err)
	}
	var underlayConnections atomic.Int32
	client.dialer = easyTierLoopbackDialer{connections: &underlayConnections}
	// The structured Mihomo options do not expose STUN server lists. Override
	// only those test-fixture lists, retaining the real outbound startup path.
	client.configTOML = "stun_servers = []\nstun_servers_v6 = []\n" + client.configTOML

	// Start through the same bounded background coordinator used by a proven
	// LAN rule. No business DialContext or payload has occurred at this point.
	prepared := make(chan error, 1)
	manager := lanpreconnect.New()
	t.Cleanup(manager.Close)
	manager.Replace([]lanpreconnect.Intent{{Outbound: &easyTierObservedPreparation{EasyTier: client, completed: prepared}, Target: businessIP}})
	started := time.Now()

	delay := time.NewTimer(routeDelay)
	defer delay.Stop()
	poll := time.NewTicker(100 * time.Millisecond)
	defer poll.Stop()
	minimumDelayPassed, relayVisible := false, false
	for !minimumDelayPassed || !relayVisible {
		select {
		case early := <-prepared:
			t.Fatalf("background preparation completed before target route admission (%s): %v", time.Since(started), early)
		case <-delay.C:
			minimumDelayPassed = true
		case <-poll.C:
			instance, instanceErr := client.currentInstance()
			if instanceErr != nil || instance.State() != corehost.StateRunning {
				continue
			}
			probe, stop := context.WithTimeout(ctx, 200*time.Millisecond)
			routes, routeErr := instance.ListRoute(probe)
			stop()
			if routeErr == nil {
				if easyTierRouteTo(routes, businessIP) != nil {
					t.Fatal("target route appeared before its loopback connection was admitted")
				}
				relayVisible = easyTierRouteTo(routes, relayIP) != nil
			}
		case <-ctx.Done():
			t.Fatalf("wait for a running client with relay but no target route: %v", ctx.Err())
		}
	}
	if underlayConnections.Load() == 0 {
		t.Fatal("background startup did not establish a real relay connection before business")
	}
	select {
	case early := <-prepared:
		t.Fatalf("target was declared ready before admission: %v", early)
	default:
	}
	admittedAfter := time.Since(started)
	close(admitTarget)
	select {
	case err := <-prepared:
		if err != nil {
			t.Fatalf("background preparation failed: %v", err)
		}
	case <-ctx.Done():
		t.Fatalf("background preparation did not finish: %v", ctx.Err())
	}
	if time.Since(started) < routeDelay {
		t.Fatal("fixture did not exercise startup beyond five seconds")
	}
	if sockets.lanConnections.Load() != 0 {
		t.Fatal("preparation contacted a LAN application port")
	}
	// The first business connection gets only the ordinary five seconds and
	// reuses the already-established native relay path.
	dialCtx, stopDial := context.WithTimeout(ctx, C.DefaultTCPTimeout)
	defer stopDial()
	businessStarted := time.Now()
	connection, err = client.DialContext(dialCtx, &C.Metadata{NetWork: C.TCP, DstIP: businessIP, DstPort: uint16(listener.Addr().(*net.TCPAddr).Port)})
	if err != nil {
		t.Fatalf("first business dial after background preparation: %v (LAN socket calls=%d)", err, sockets.lanConnections.Load())
	}
	if time.Since(businessStarted) >= C.DefaultTCPTimeout {
		t.Fatal("business dial exceeded ordinary timeout")
	}
	if err = connection.SetDeadline(time.Now().Add(5 * time.Second)); err != nil {
		t.Fatalf("set client TCP deadline: %v", err)
	}

	request, response := []byte("terraria-first-connect-request"), []byte("target-through-relay-response")
	served := make(chan error, 1)
	go func() {
		peer, acceptErr := listener.Accept()
		if acceptErr != nil {
			served <- acceptErr
			return
		}
		defer peer.Close()
		_ = peer.SetDeadline(time.Now().Add(5 * time.Second))
		payload := make([]byte, len(request))
		if _, readErr := io.ReadFull(peer, payload); readErr != nil {
			served <- readErr
			return
		}
		if !bytes.Equal(payload, request) {
			served <- fmt.Errorf("target received %q, want %q", payload, request)
			return
		}
		_, writeErr := peer.Write(response)
		served <- writeErr
	}()
	if _, err = connection.Write(request); err != nil {
		t.Fatalf("write client-to-target TCP payload: %v", err)
	}
	payload := make([]byte, len(response))
	if _, err = io.ReadFull(connection, payload); err != nil || !bytes.Equal(payload, response) {
		t.Fatalf("read target-to-client TCP response: payload=%q error=%v", payload, err)
	}
	select {
	case err = <-served:
		if err != nil {
			t.Fatalf("target TCP exchange failed: %v", err)
		}
	case <-ctx.Done():
		t.Fatalf("target TCP exchange did not finish: %v", ctx.Err())
	}
	instance, err := client.currentInstance()
	if err != nil {
		t.Fatal(err)
	}
	routes, err := instance.ListRoute(ctx)
	if err != nil {
		t.Fatalf("read established client routing table: %v", err)
	}
	route := easyTierRouteTo(routes, businessIP)
	if route == nil || route.GetNextHopPeerId() != relayInfo.GetPeerId() || route.GetNextHopPeerId() == route.GetPeerId() {
		t.Fatalf("TCP target route must use the relay, without a direct target connection: relay=%d route=%v", relayInfo.GetPeerId(), route)
	}
	if subnet && sockets.lanConnections.Load() != 1 {
		t.Fatalf("LAN host socket count = %d, want one business connection", sockets.lanConnections.Load())
	}
	t.Logf("background relay preparation before business: admitted after %s; total elapsed %s; relay=%d target=%d next_hop=%d; exchanged %d/%d bytes",
		admittedAfter, time.Since(started), relayInfo.GetPeerId(), route.GetPeerId(), route.GetNextHopPeerId(), len(request), len(response))
}

func easyTierListenTCP(ctx context.Context, instance *corehost.Instance) (net.Listener, error) {
	poll := time.NewTicker(20 * time.Millisecond)
	defer poll.Stop()
	for {
		listener, err := instance.Listen("tcp4", ":0")
		if err == nil || !errors.Is(err, syscall.ENETUNREACH) {
			return listener, err
		}
		select {
		case <-poll.C:
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
}

func easyTierRouteTo(routes []*corehost.Route, address netip.Addr) *corehost.Route {
	var selected *corehost.Route
	bits := -1
	for _, route := range routes {
		if ipv4 := route.GetIpv4Addr().GetAddress(); ipv4 != nil && easytier.IPv4FromUint32(ipv4.GetAddr()) == address {
			return route
		}
		for _, cidr := range route.GetProxyCidrs() {
			if prefix, err := netip.ParsePrefix(cidr); err == nil && prefix.Contains(address) && prefix.Bits() > bits {
				selected, bits = route, prefix.Bits()
			}
		}
	}
	return selected
}

type easyTierRelaySockets struct {
	inner          netstd.SocketFactory
	admit          <-chan struct{}
	listening      chan int
	attempted      chan struct{}
	once           sync.Once
	lanTarget      netip.AddrPort
	lanLoopback    *net.TCPAddr
	lanConnections atomic.Int32
}

func (s *easyTierRelaySockets) ConnectTCP(ctx context.Context, options platform.TCPConnectOptions) (net.Conn, error) {
	if options.RemoteAddr != nil && s.lanTarget.IsValid() && netip.AddrPortFrom(options.RemoteAddr.AddrPort().Addr().Unmap(), uint16(options.RemoteAddr.Port)) == s.lanTarget {
		if options.Purpose != platform.TCPConnectProxyNAT {
			return nil, fmt.Errorf("LAN socket has unexpected purpose %v", options.Purpose)
		}
		s.lanConnections.Add(1)
		options.RemoteAddr = s.lanLoopback
		return s.inner.ConnectTCP(ctx, options)
	}
	if options.RemoteAddr == nil || !options.RemoteAddr.IP.IsLoopback() {
		return nil, errors.New("integration test forbids non-loopback TCP")
	}
	s.once.Do(func() { close(s.attempted) })
	select {
	case <-s.admit:
		return s.inner.ConnectTCP(ctx, options)
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func (s *easyTierRelaySockets) ListenTCP(ctx context.Context, options platform.TCPListenOptions) (net.Listener, error) {
	listener, err := s.inner.ListenTCP(ctx, options)
	if err == nil && listener.Addr().(*net.TCPAddr).IP.IsLoopback() {
		select {
		case s.listening <- listener.Addr().(*net.TCPAddr).Port:
		default:
		}
	}
	return listener, err
}

func (s *easyTierRelaySockets) BindUDP(ctx context.Context, options platform.UDPBindOptions) (net.PacketConn, error) {
	packet, err := s.inner.BindUDP(ctx, options)
	if err != nil {
		return nil, err
	}
	return easyTierLocalPackets{packet}, nil
}

type easyTierObservedPreparation struct {
	*EasyTier
	completed chan<- error
}

func (p *easyTierObservedPreparation) PrepareLAN(ctx context.Context, target netip.Addr) error {
	err := p.EasyTier.PrepareLAN(ctx, target)
	select {
	case p.completed <- err:
	default:
	}
	return err
}

type easyTierLoopbackDialer struct{ connections *atomic.Int32 }

func (d easyTierLoopbackDialer) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	peer, err := netip.ParseAddrPort(address)
	if err != nil || !peer.Addr().IsLoopback() {
		return nil, fmt.Errorf("integration test forbids non-loopback dial %q", address)
	}
	conn, err := (&net.Dialer{}).DialContext(ctx, network, address)
	if err == nil && d.connections != nil {
		d.connections.Add(1)
	}
	return conn, err
}

func (easyTierLoopbackDialer) ListenPacket(ctx context.Context, network, address string, _ netip.AddrPort) (net.PacketConn, error) {
	packet, err := (&net.ListenConfig{}).ListenPacket(ctx, network, address)
	if err != nil {
		return nil, err
	}
	return easyTierLocalPackets{packet}, nil
}

type easyTierLocalPackets struct{ net.PacketConn }

func (p easyTierLocalPackets) WriteTo(payload []byte, target net.Addr) (int, error) {
	peer, ok := target.(*net.UDPAddr)
	if !ok || !peer.IP.IsLoopback() {
		return 0, errors.New("integration test forbids non-loopback UDP")
	}
	return p.PacketConn.WriteTo(payload, target)
}
