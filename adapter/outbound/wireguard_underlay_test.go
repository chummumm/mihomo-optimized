package outbound

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"testing"
	"time"

	C "github.com/metacubex/mihomo/constant"
)

type lanBlockingCarrier struct{ observed chan time.Time }

func (d lanBlockingCarrier) wait(ctx context.Context) error {
	deadline, _ := ctx.Deadline()
	d.observed <- deadline
	<-ctx.Done()
	return ctx.Err()
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
