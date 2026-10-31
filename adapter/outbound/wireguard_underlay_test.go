package outbound

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/mihomo/component/proxydialer"
	C "github.com/metacubex/mihomo/constant"
	wireguard "github.com/metacubex/sing-wireguard"
)

type lanFixedCarrier struct{ net.Conn }

func (d lanFixedCarrier) DialContext(context.Context, string, string) (net.Conn, error) {
	return d.Conn, nil
}
func (d lanFixedCarrier) ListenPacket(context.Context, string, string, netip.AddrPort) (net.PacketConn, error) {
	return nil, errors.New("unexpected ListenPacket")
}

type lanWriteSignalConn struct {
	net.Conn
	started chan struct{}
	once    sync.Once
}

func (c *lanWriteSignalConn) Write(b []byte) (int, error) {
	c.once.Do(func() { close(c.started) })
	return c.Conn.Write(b)
}

// Model native SendBuffers/BindClose's lock order around the real ClientBind.
// A context cancellation alone does not release the established carrier write.
type lanNativeBindDevice struct {
	bind *wireGuardClientBind
	net  sync.RWMutex
}

func (d *lanNativeBindDevice) IpcSet(string) error { return nil }
func (d *lanNativeBindDevice) Close() {
	d.net.Lock()
	defer d.net.Unlock()
	_ = d.bind.Close()
}

func TestLANWireGuardCloseInterruptsCarrierBeforeNativeNetworkLock(t *testing.T) {
	lifetime, stop := context.WithCancel(context.Background())
	defer stop()
	carrier, remote := net.Pipe()
	defer carrier.Close()
	defer remote.Close()
	started := make(chan struct{})
	dialer := proxydialer.NewSingDialer(lanFixedCarrier{&lanWriteSignalConn{Conn: carrier, started: started}})
	endpoint := netip.MustParseAddrPort("127.0.0.1:12345")
	bind := &wireGuardClientBind{
		ClientBind: wireguard.NewClientBind(lifetime, wgSingErrorHandler{"test"}, dialer, true, endpoint, [3]uint8{}),
		ctx:        lifetime,
	}
	if _, _, err := bind.Open(0); err != nil {
		t.Fatal(err)
	}
	native := &lanNativeBindDevice{bind: bind}
	w := &WireGuard{runCtx: lifetime, runCancel: stop, bind: bind, device: native}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	waiter, controlDone := make(chan error, 1), make(chan struct{})
	go func() {
		waiter <- w.runLANControl(ctx, "peer", func() error {
			defer close(controlDone)
			native.net.RLock()
			defer native.net.RUnlock()
			return bind.Send([][]byte{{1, 0, 0, 0}}, wireguard.Endpoint(endpoint))
		})
	}()
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("native control write did not reach its carrier")
	}
	cancel()
	if err := <-waiter; !errors.Is(err, context.Canceled) {
		t.Fatalf("waiter did not cancel: %v", err)
	}
	closed := make(chan struct{})
	go func() { _ = w.Close(); close(closed) }()
	select {
	case <-closed:
	case <-time.After(time.Second):
		_ = carrier.Close() // Release the test goroutine before reporting failure.
		<-closed
		t.Fatal("native Close waited for a write without closing its carrier")
	}
	select {
	case <-controlDone:
	case <-time.After(time.Second):
		t.Fatal("instance close left its control write alive")
	}
	if _, _, err := bind.Open(0); !errors.Is(err, context.Canceled) {
		t.Fatalf("late EventUp reopened a closed instance: %v", err)
	}
}

type lanBlockingCarrier struct{ observed chan time.Time }

func (d lanBlockingCarrier) wait(ctx context.Context) error {
	deadline, _ := ctx.Deadline()
	d.observed <- deadline
	<-ctx.Done()
	return ctx.Err()
}

func TestLANWireGuardNativeKickWaitersCancelWithoutDuplicatingWork(t *testing.T) {
	lifetime, stop := context.WithCancel(context.Background())
	defer stop()
	w := &WireGuard{runCtx: lifetime, runCancel: stop}
	started, exited := make(chan struct{}), make(chan struct{})
	var calls atomic.Int32
	kick := func() error { calls.Add(1); close(started); <-lifetime.Done(); close(exited); return lifetime.Err() }
	first, cancel := context.WithCancel(context.Background())
	firstDone := make(chan error, 1)
	go func() { firstDone <- w.runLANControl(first, "peer", kick) }()
	<-started
	cancel()
	if err := <-firstDone; !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	second, stopSecond := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer stopSecond()
	if err := w.runLANControl(second, "peer", kick); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("second waiter=%v", err)
	}
	if calls.Load() != 1 {
		t.Fatal("canceled generations spawned duplicate native kicks")
	}
	if err := w.Close(); err != nil {
		t.Fatal(err)
	}
	select {
	case <-exited:
	case <-time.After(time.Second):
		t.Fatal("instance close left native kick alive")
	}
}

func TestLANWireGuardDifferentPeerWaitsThenGetsItsOwnKick(t *testing.T) {
	lifetime, stop := context.WithCancel(context.Background())
	defer stop()
	w := &WireGuard{runCtx: lifetime, runCancel: stop}
	firstStarted, releaseFirst, secondStarted := make(chan struct{}), make(chan struct{}), make(chan struct{})
	results := make(chan error, 2)
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	go func() {
		results <- w.runLANControl(ctx, "first-peer", func() error {
			close(firstStarted)
			select {
			case <-releaseFirst:
				return nil
			case <-lifetime.Done():
				return lifetime.Err()
			}
		})
	}()
	<-firstStarted
	go func() {
		results <- w.runLANControl(ctx, "second-peer", func() error { close(secondStarted); return nil })
	}()
	select {
	case <-secondStarted:
		t.Fatal("different peers prepared concurrently")
	case <-time.After(20 * time.Millisecond):
	}
	close(releaseFirst)
	for i := 0; i < 2; i++ {
		if err := <-results; err != nil {
			t.Fatal(err)
		}
	}
	select {
	case <-secondStarted:
	default:
		t.Fatal("first peer completion was mistaken for second peer readiness")
	}
}
func (d lanBlockingCarrier) DialContext(ctx context.Context, _, _ string) (net.Conn, error) {
	return nil, d.wait(ctx)
}
func (d lanBlockingCarrier) ListenPacket(ctx context.Context, _, _ string, _ netip.AddrPort) (net.PacketConn, error) {
	return nil, d.wait(ctx)
}

func TestLANWireGuardCarrierCreationIsBoundedAndParentCanceled(t *testing.T) {
	for _, packet := range []bool{false, true} {
		for _, early := range []bool{false, true} {
			observed := make(chan time.Time, 1)
			dialer := wireGuardUnderlayDialer{lanBlockingCarrier{observed}}
			parent, cancel := context.WithCancel(context.Background())
			var expected time.Time
			if early {
				cancel()
				expected = time.Now().Add(100 * time.Millisecond)
				parent, cancel = context.WithDeadline(context.Background(), expected)
			}
			started := time.Now()
			result := make(chan error, 1)
			go func() {
				if packet {
					_, err := dialer.ListenPacket(parent, "udp", "", netip.AddrPort{})
					result <- err
				} else {
					_, err := dialer.DialContext(parent, "udp", "127.0.0.1:12345")
					result <- err
				}
			}()
			deadline := <-observed
			if early {
				if !deadline.Equal(expected) {
					t.Fatal("carrier replaced earlier parent deadline")
				}
			} else if budget := deadline.Sub(started); budget < C.DefaultTCPTimeout-100*time.Millisecond || budget > C.DefaultTCPTimeout+100*time.Millisecond {
				t.Fatalf("carrier has unbounded/shared lifetime: %s", budget)
			}
			cancel()
			select {
			case err := <-result:
				if !errors.Is(err, context.Canceled) {
					t.Fatalf("carrier cancellation=%v", err)
				}
			case <-time.After(time.Second):
				t.Fatal("carrier ignored parent cancellation")
			}
		}
	}
}
