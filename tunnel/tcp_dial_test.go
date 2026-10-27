package tunnel

import (
	"context"
	"net"
	"net/netip"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	N "github.com/metacubex/mihomo/common/net"
	C "github.com/metacubex/mihomo/constant"
	icontext "github.com/metacubex/mihomo/context"
)

func tcpDialTestState(t *testing.T, dial func(context.Context, *C.Metadata) (C.Conn, error)) {
	t.Helper()
	dnsProxyTestState(t)
	previousStatus, previousSniffing := status.Load(), sniffingEnable
	status.Store(Running)
	sniffingEnable = false
	t.Cleanup(func() { status.Store(previousStatus); sniffingEnable = previousSniffing })
	proxies["DIRECT"] = newDNSProxyTestProxy(&dnsProxyTestAdapter{
		dnsProxyTestBase: newDNSProxyTestBase("DIRECT", C.Direct, true),
		dial:             dial,
	})
	rules = []C.Rule{dnsProxyTestRule(t, "MATCH,DIRECT")}
}

func tcpDialTestMetadata() *C.Metadata {
	return &C.Metadata{NetWork: C.TCP, Type: C.INNER,
		SrcIP: netip.MustParseAddr("127.0.0.1"), SrcPort: 52000,
		DstIP: netip.MustParseAddr("10.78.0.2"), DstPort: 7777}
}

func TestTCPDialBudgetExistingInboundLifetimeCancelsPendingDial(t *testing.T) {
	started := make(chan struct{})
	var calls atomic.Int32
	tcpDialTestState(t, func(ctx context.Context, _ *C.Metadata) (C.Conn, error) {
		if calls.Add(1) == 1 {
			close(started)
		}
		<-ctx.Done()
		return nil, ctx.Err()
	})
	inbound, client := net.Pipe()
	defer client.Close()
	parent, cancel := context.WithCancel(context.Background())
	defer cancel()
	finished := make(chan struct{})
	go func() {
		Tunnel.HandleTCPConnContext(parent, inbound, tcpDialTestMetadata())
		close(finished)
	}()
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("TCP handler did not begin the dial")
	}
	cancel()
	select {
	case <-finished:
	case <-time.After(time.Second):
		t.Fatal("inbound lifetime was replaced with context.Background")
	}
	if calls.Load() != 1 {
		t.Fatalf("canceled request started more attempts: %d", calls.Load())
	}
}

type tcpDialBlockedHandshake struct {
	*dnsProxyTestConn
	started chan struct{}
	closed  chan struct{}
	once    sync.Once
}

func (c *tcpDialBlockedHandshake) NeedHandshake() bool { return true }
func (c *tcpDialBlockedHandshake) Write([]byte) (int, error) {
	close(c.started)
	<-c.closed
	return 0, net.ErrClosed
}
func (c *tcpDialBlockedHandshake) Close() error {
	c.once.Do(func() { close(c.closed) })
	return c.ExtendedConn.Close()
}

func TestTCPDialBudgetCancelsBlockedFirstWriteAndClosesConnection(t *testing.T) {
	remote, server := net.Pipe()
	defer server.Close()
	conn := &tcpDialBlockedHandshake{
		dnsProxyTestConn: &dnsProxyTestConn{ExtendedConn: N.NewExtendedConn(remote)},
		started:          make(chan struct{}), closed: make(chan struct{}),
	}
	tcpDialTestState(t, func(context.Context, *C.Metadata) (C.Conn, error) { return conn, nil })
	inbound, client := net.Pipe()
	defer client.Close()
	parent, cancel := context.WithCancel(context.Background())
	defer cancel()
	finished := make(chan struct{})
	go func() {
		Tunnel.HandleTCPConnContext(parent, inbound, tcpDialTestMetadata())
		close(finished)
	}()
	select {
	case <-conn.started:
	case <-time.After(time.Second):
		t.Fatal("first-write handshake did not start")
	}
	cancel()
	select {
	case <-finished:
	case <-time.After(time.Second):
		t.Fatal("dial cancellation did not interrupt the first-write handshake")
	}
	select {
	case <-conn.closed:
	default:
		t.Fatal("canceled first-write connection leaked")
	}
}

func TestTCPDialBudgetRetryDoesNotStartAfterCancellation(t *testing.T) {
	ctx, cancel := icontext.WithTCPDialBudget(context.Background())
	cancel()
	calls := 0
	_, err := retry(ctx, func(context.Context) (int, error) { calls++; return 1, nil }, nil)
	if err != context.Canceled || calls != 0 {
		t.Fatalf("canceled request ran another attempt: calls=%d err=%v", calls, err)
	}
}
