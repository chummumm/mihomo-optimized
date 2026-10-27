//go:build !no_easytier

package outbound

import (
	"context"
	"encoding/binary"
	"errors"
	"net"
	"net/netip"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"

	corehost "github.com/easytier/easytier/easytier-go"
	protoCommon "github.com/easytier/easytier/easytier-go/proto/common"
)

type easyTierReadyInstance struct {
	state      atomic.Int32
	mu         sync.Mutex
	info       *corehost.NodeInfo
	routes     []*corehost.Route
	nextRoutes []*corehost.Route
	peers      []net.Conn
	stateRead  chan struct{}
	stateAllow chan struct{}
	stateOnce  sync.Once
	rpcStart   chan struct{}
	rpcAllow   chan struct{}
	rpcOnce    sync.Once
	rpcCount   atomic.Int32
	dials      atomic.Int32
}

func (f *easyTierReadyInstance) State() corehost.State {
	state := corehost.State(f.state.Load())
	if f.stateRead != nil {
		f.stateOnce.Do(func() {
			close(f.stateRead)
			<-f.stateAllow
		})
	}
	return state
}
func (f *easyTierReadyInstance) Wait(ctx context.Context) error {
	<-ctx.Done()
	return ctx.Err()
}
func (f *easyTierReadyInstance) Events() <-chan corehost.Event { return nil }
func (f *easyTierReadyInstance) ShowNodeInfo(ctx context.Context) (*corehost.NodeInfo, error) {
	f.rpcCount.Add(1)
	if f.rpcStart != nil {
		f.rpcOnce.Do(func() { close(f.rpcStart) })
	}
	if f.rpcAllow != nil {
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-f.rpcAllow:
		}
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.info, nil
}
func (f *easyTierReadyInstance) ListRoute(context.Context) ([]*corehost.Route, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	routes := append([]*corehost.Route(nil), f.routes...)
	if f.nextRoutes != nil {
		f.routes, f.nextRoutes = f.nextRoutes, nil
	}
	return routes, nil
}
func (f *easyTierReadyInstance) Dial(ctx context.Context, _, _ string) (net.Conn, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	f.dials.Add(1)
	client, server := net.Pipe()
	f.mu.Lock()
	f.peers = append(f.peers, server)
	f.mu.Unlock()
	return client, nil
}
func (f *easyTierReadyInstance) ListenPacket(string, string) (net.PacketConn, error) {
	return nil, C.ErrNotSupport
}
func (f *easyTierReadyInstance) Close(context.Context) error {
	f.state.Store(int32(corehost.StateStopped))
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, peer := range f.peers {
		_ = peer.Close()
	}
	return nil
}

func easyTierTestRoute(ip string, peer, nextHop uint32) *corehost.Route {
	ipv4 := netip.MustParseAddr(ip).As4()
	return &corehost.Route{
		PeerId: peer, NextHopPeerId: nextHop, Cost: 1,
		Ipv4Addr: &protoCommon.Ipv4Inet{Address: &protoCommon.Ipv4Addr{Addr: binary.BigEndian.Uint32(ipv4[:])}, NetworkLength: 24},
	}
}

func newEasyTierReadyTest(t *testing.T) (*EasyTier, *easyTierReadyInstance) {
	t.Helper()
	ctx, cancel := context.WithCancel(context.Background())
	f := &easyTierReadyInstance{info: &corehost.NodeInfo{PeerId: 1, Ipv4Addr: "10.78.0.1/24"}}
	f.state.Store(int32(corehost.StateRunning))
	e := &EasyTier{
		Base: NewBase(BaseOption{Name: "test-easytier", Type: C.EasyTier}),
		ctx:  ctx, cancel: cancel, zone: "et.net", instance: f,
		readyCh: make(chan struct{}), loopDone: make(chan struct{}),
	}
	// The fake represents the owned runtime; no real WASM process is launched.
	e.loopOnce.Do(func() { close(e.loopDone) })
	t.Cleanup(func() { _ = e.Close() })
	return e, f
}

func TestEasyTierReadyNotificationCannotBeLostBetweenCheckAndWait(t *testing.T) {
	e, f := newEasyTierReadyTest(t)
	f.state.Store(int32(corehost.StateStarting))
	f.stateRead, f.stateAllow = make(chan struct{}), make(chan struct{})
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	result := make(chan error, 1)
	go func() { result <- e.ensureStarted(ctx) }()
	<-f.stateRead
	f.state.Store(int32(corehost.StateRunning))
	e.signalReady()
	close(f.stateAllow) // State returns the old Starting observation this once.
	if err := <-result; err != nil {
		t.Fatalf("Running was published between check and wait, but waiter missed it: %v", err)
	}
}

func TestEasyTierFirstDialWaitsPastFiveSecondsForTargetRoute(t *testing.T) {
	if testing.Short() {
		t.Skip("exercises the real five-second regression boundary")
	}
	e, f := newEasyTierReadyTest(t)
	ctx, cancel := icontext.WithTCPDialBudget(context.Background())
	defer cancel()
	started := time.Now()
	routeReady := make(chan struct{})
	go func() {
		timer := time.NewTimer(5200 * time.Millisecond)
		defer timer.Stop()
		select {
		case <-ctx.Done():
			close(routeReady)
			return
		case <-timer.C:
		}
		f.mu.Lock()
		f.routes = []*corehost.Route{easyTierTestRoute("10.78.0.2", 2, 3)}
		f.mu.Unlock()
		// Deliberately omit peer/route events to exercise periodic recovery.
		close(routeReady)
	}()
	conn, err := e.DialContext(ctx, &C.Metadata{DstIP: netip.MustParseAddr("10.78.0.2"), DstPort: 7777})
	<-routeReady
	if err != nil {
		t.Fatalf("first dial failed before route convergence: %v", err)
	}
	defer conn.Close()
	if time.Since(started) <= 5*time.Second || f.dials.Load() != 1 {
		t.Fatalf("expected one actual dial after >5s, got %s, %d", time.Since(started), f.dials.Load())
	}
	// Ending the dial scope must not close a successfully established stream.
	cancel()
	f.mu.Lock()
	peer := f.peers[0]
	f.mu.Unlock()
	written := make(chan error, 1)
	go func() { _, err := peer.Write([]byte{42}); written <- err }()
	_ = conn.SetReadDeadline(time.Now().Add(time.Second))
	buf := make([]byte, 1)
	if _, err := conn.Read(buf); err != nil || buf[0] != 42 {
		t.Fatalf("dial cancellation reached the established stream: %v", err)
	}
	if err := <-written; err != nil {
		t.Fatal(err)
	}
}

func TestEasyTierRouteReadinessAcceptsRelayAndRespectsDestination(t *testing.T) {
	zero, relay := uint32(0), uint32(8)
	for _, test := range []struct {
		name, target string
		routes       []*corehost.Route
		option       EasyTierOption
		want         bool
	}{
		{"only relay", "10.78.0.2", []*corehost.Route{easyTierTestRoute("10.78.0.2", 2, 3)}, EasyTierOption{}, true},
		{"only unrelated peer", "10.78.0.2", []*corehost.Route{easyTierTestRoute("10.78.0.3", 3, 3)}, EasyTierOption{}, false},
		{"same virtual prefix is not a route", "10.78.0.2", []*corehost.Route{easyTierTestRoute("10.78.0.8", 8, 8)}, EasyTierOption{}, false},
		{"missing next hop", "10.78.0.2", []*corehost.Route{easyTierTestRoute("10.78.0.2", 2, 0)}, EasyTierOption{}, false},
		{"local endpoint", "10.78.0.1", nil, EasyTierOption{}, true},
		{"exit for public destination", "203.0.113.7", []*corehost.Route{easyTierTestRoute("10.78.0.3", 3, 8)}, EasyTierOption{ExitNodes: []string{"10.78.0.3"}}, true},
		{"exit cannot replace missing overlay route", "10.78.0.2", []*corehost.Route{easyTierTestRoute("10.78.0.3", 3, 8)}, EasyTierOption{ExitNodes: []string{"10.78.0.3"}}, false},
		{"exit not configured", "203.0.113.7", []*corehost.Route{easyTierTestRoute("10.78.0.3", 3, 8)}, EasyTierOption{}, false},
		{"proxy subnet", "192.168.42.7", []*corehost.Route{{PeerId: 2, NextHopPeerId: 3, ProxyCidrs: []string{"192.168.42.0/24"}}}, EasyTierOption{}, true},
		{"latency policy not ready", "192.168.42.7", []*corehost.Route{{PeerId: 2, NextHopPeerId: 3, NextHopPeerIdLatencyFirst: &zero, ProxyCidrs: []string{"192.168.42.0/24"}}}, EasyTierOption{LatencyFirst: boolPointer(true)}, false},
		{"latency policy relay ready", "192.168.42.7", []*corehost.Route{{PeerId: 2, NextHopPeerIdLatencyFirst: &relay, ProxyCidrs: []string{"192.168.42.0/24"}}}, EasyTierOption{LatencyFirst: boolPointer(true)}, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			snapshot := &easyTierRouteSnapshot{info: &corehost.NodeInfo{PeerId: 1, Ipv4Addr: "10.78.0.1/24"}, routes: test.routes}
			if got := snapshot.readyFor(netip.MustParseAddr(test.target), test.option); got != test.want {
				t.Fatalf("ready = %v, want %v", got, test.want)
			}
		})
	}
}

func boolPointer(value bool) *bool { return &value }

func TestEasyTierWaitingHostnameFollowsChangedRouteAddress(t *testing.T) {
	for _, host := range []string{"target.et.net", "target"} {
		t.Run(host, func(t *testing.T) {
			e, f := newEasyTierReadyTest(t)
			oldRoute := easyTierTestRoute("10.78.0.2", 2, 0)
			oldRoute.Hostname = "target"
			newRoute := easyTierTestRoute("10.78.0.3", 2, 3)
			newRoute.Hostname = "target"
			// The first snapshot resolves the name but has no path. Before
			// the next snapshot its address changes and the relay is ready.
			f.routes, f.nextRoutes = []*corehost.Route{oldRoute}, []*corehost.Route{newRoute}
			ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
			defer cancel()
			instance, ip, err := e.prepareTarget(ctx, host)
			if err != nil || instance != f || ip.String() != "10.78.0.3" {
				t.Fatalf("waited for an obsolete hostname address: instance=%v ip=%v err=%v", instance == f, ip, err)
			}
			if f.rpcCount.Load() < 2 {
				t.Fatal("hostname test did not observe the address transition")
			}
		})
	}
}

func TestEasyTierRunningWithoutIPv4DoesNotDial(t *testing.T) {
	e, f := newEasyTierReadyTest(t)
	f.info = &corehost.NodeInfo{PeerId: 1}
	f.routes = []*corehost.Route{easyTierTestRoute("10.78.0.2", 2, 3)}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	defer cancel()
	_, err := e.DialContext(ctx, &C.Metadata{DstIP: netip.MustParseAddr("10.78.0.2"), DstPort: 7777})
	if !errors.Is(err, context.DeadlineExceeded) || f.dials.Load() != 0 {
		t.Fatalf("dialed before DHCP was ready: %v, calls %d", err, f.dials.Load())
	}
}

func TestEasyTierRouteRefreshIsSharedAndWaitersCancelIndependently(t *testing.T) {
	e, f := newEasyTierReadyTest(t)
	f.rpcStart, f.rpcAllow = make(chan struct{}), make(chan struct{})
	f.routes = []*corehost.Route{easyTierTestRoute("10.78.0.2", 2, 3)}
	first, stopFirst := context.WithCancel(context.Background())
	second, stopSecond := context.WithTimeout(context.Background(), time.Second)
	defer stopSecond()
	errorsOut := make(chan error, 2)
	go func() { _, err := e.routeSnapshot(first, f); errorsOut <- err }()
	<-f.rpcStart
	go func() { _, err := e.routeSnapshot(second, f); errorsOut <- err }()
	stopFirst()
	if err := <-errorsOut; !errors.Is(err, context.Canceled) {
		t.Fatalf("first waiter did not cancel independently: %v", err)
	}
	close(f.rpcAllow)
	if err := <-errorsOut; err != nil {
		t.Fatalf("shared RPC was canceled with the first waiter: %v", err)
	}
	if f.rpcCount.Load() != 1 || e.ctx.Err() != nil {
		t.Fatalf("duplicate or canceled runtime refresh: %d, %v", f.rpcCount.Load(), e.ctx.Err())
	}
}

func TestEasyTierCloseUnblocksRuntimeAndRouteWaiters(t *testing.T) {
	for _, running := range []bool{false, true} {
		e, f := newEasyTierReadyTest(t)
		if !running {
			f.state.Store(int32(corehost.StateStarting))
		}
		ctx, cancel := context.WithTimeout(context.Background(), time.Second)
		result := make(chan error, 1)
		go func() { _, _, err := e.prepareTarget(ctx, "10.78.0.2"); result <- err }()
		if err := e.Close(); err != nil {
			t.Fatal(err)
		}
		if err := <-result; !errors.Is(err, errEasyTierClosed) {
			t.Fatalf("close did not wake pending work: %v", err)
		}
		cancel()
	}
}
