package tunnel

import (
	"context"
	"errors"
	"net"
	"sync/atomic"
	"syscall"
	"testing"
	"time"

	N "github.com/metacubex/mihomo/common/net"
	C "github.com/metacubex/mihomo/constant"
)

// Like the TUN and SOCKS lazy inbounds, a real Read accepts this handshake.
// A speculative tunnel Peek must therefore never run while it is pending.
type tcpHandshakeInbound struct {
	net.Conn
	pending  atomic.Bool
	reads    atomic.Int32
	success  atomic.Int32
	failure  atomic.Int32
	drops    atomic.Int32
	closed   atomic.Bool
	late     atomic.Bool
	reported error
}

func newTCPHandshakeInbound(conn net.Conn) *tcpHandshakeInbound {
	c := &tcpHandshakeInbound{Conn: conn}
	c.pending.Store(true)
	return c
}

func (c *tcpHandshakeInbound) NeedHandshake() bool { return c.pending.Load() }
func (c *tcpHandshakeInbound) HandshakeSuccess() error {
	if c.pending.Swap(false) {
		c.success.Add(1)
	}
	return nil
}
func (c *tcpHandshakeInbound) HandshakeFailure(err error) error {
	if c.closed.Load() {
		c.late.Store(true)
	}
	c.failure.Add(1)
	c.reported = err
	c.pending.Store(false)
	return nil
}
func (c *tcpHandshakeInbound) HandshakeDrop() error {
	c.drops.Add(1)
	c.pending.Store(false)
	return nil
}
func (c *tcpHandshakeInbound) Read(p []byte) (int, error) {
	c.reads.Add(1)
	_ = c.HandshakeSuccess()
	return c.Conn.Read(p)
}
func (c *tcpHandshakeInbound) Close() error {
	c.closed.Store(true)
	return c.Conn.Close()
}

func TestTCPHandshakeDialFailureDoesNotAcknowledgeOrPeek(t *testing.T) {
	tcpDialTestState(t, func(context.Context, *C.Metadata) (C.Conn, error) {
		return nil, C.ErrTargetIPFamily // Terminal error: no retry delay.
	})
	inbound, client := net.Pipe()
	defer client.Close()
	reporter := newTCPHandshakeInbound(inbound)
	Tunnel.HandleTCPConn(reporter, tcpDialTestMetadata())
	if reporter.success.Load() != 0 || reporter.reads.Load() != 0 {
		t.Fatalf("failed outbound was acknowledged: success=%d reads=%d", reporter.success.Load(), reporter.reads.Load())
	}
	if reporter.failure.Load() != 1 || reporter.late.Load() || !errors.Is(reporter.reported, C.ErrTargetIPFamily) {
		t.Fatalf("failure was not reported before close: failures=%d late=%v err=%v", reporter.failure.Load(), reporter.late.Load(), reporter.reported)
	}
}

type tcpRelayFailure struct{ net.Conn }

func (tcpRelayFailure) Read([]byte) (int, error) { return 0, syscall.ECONNRESET }

func TestTCPHandshakeRelayFailureNeverWritesAnotherProtocolReply(t *testing.T) {
	remote, server := net.Pipe()
	defer server.Close()
	tcpDialTestState(t, func(context.Context, *C.Metadata) (C.Conn, error) {
		return &dnsProxyTestConn{ExtendedConn: N.NewExtendedConn(tcpRelayFailure{remote})}, nil
	})
	inbound, client := net.Pipe()
	defer client.Close()
	reporter := newTCPHandshakeInbound(inbound)
	finished := make(chan struct{})
	go func() { Tunnel.HandleTCPConn(reporter, tcpDialTestMetadata()); close(finished) }()
	select {
	case <-finished:
	case <-time.After(time.Second):
		t.Fatal("relay did not stop on failure")
	}
	if reporter.success.Load() != 1 || reporter.failure.Load() != 0 {
		t.Fatalf("relay error became a second handshake response: success=%d failure=%d", reporter.success.Load(), reporter.failure.Load())
	}
}

type tcpRejectedConn struct {
	*dnsProxyTestConn
	kind C.AdapterType
}

func (c *tcpRejectedConn) Rejection() C.AdapterType { return c.kind }

func tcpRejectTestDial(t *testing.T, kind C.AdapterType) func(context.Context, *C.Metadata) (C.Conn, error) {
	t.Helper()
	return func(context.Context, *C.Metadata) (C.Conn, error) {
		remote, server := net.Pipe()
		t.Cleanup(func() { _ = server.Close() })
		return &tcpRejectedConn{dnsProxyTestConn: &dnsProxyTestConn{
			ExtendedConn:           N.NewExtendedConn(remote),
			dnsProxyTestConnection: dnsProxyTestConnection{chain: C.Chain{"custom-reject", "selected-group"}},
		}, kind: kind}, nil
	}
}

func TestTCPHandshakeRejectIsNotSyntheticSuccess(t *testing.T) {
	tcpDialTestState(t, tcpRejectTestDial(t, C.Reject))
	inbound, client := net.Pipe()
	defer client.Close()
	reporter := newTCPHandshakeInbound(inbound)
	Tunnel.HandleTCPConn(reporter, tcpDialTestMetadata())
	if reporter.success.Load() != 0 || reporter.failure.Load() != 1 || reporter.drops.Load() != 0 {
		t.Fatalf("custom-named REJECT was acknowledged: success=%d failure=%d drop=%d", reporter.success.Load(), reporter.failure.Load(), reporter.drops.Load())
	}
}

func TestTCPHandshakeRejectDropDoesNotSendRejectOrSuccess(t *testing.T) {
	tcpDialTestState(t, tcpRejectTestDial(t, C.RejectDrop))
	inbound, client := net.Pipe()
	defer client.Close()
	reporter := newTCPHandshakeInbound(inbound)
	finished := make(chan struct{})
	go func() { Tunnel.HandleTCPConn(reporter, tcpDialTestMetadata()); close(finished) }()
	select {
	case <-finished:
	case <-time.After(time.Second):
		t.Fatal("unaccepted TUN request was not dropped promptly")
	}
	if reporter.success.Load() != 0 || reporter.failure.Load() != 0 || reporter.drops.Load() != 1 {
		t.Fatalf("DROP changed to accept/reset: success=%d failure=%d drop=%d", reporter.success.Load(), reporter.failure.Load(), reporter.drops.Load())
	}
}

func TestTCPHandshakeAcceptedDropRetainsSilentLifetime(t *testing.T) {
	dialed := make(chan struct{})
	dial := tcpRejectTestDial(t, C.RejectDrop)
	tcpDialTestState(t, func(ctx context.Context, metadata *C.Metadata) (C.Conn, error) {
		close(dialed)
		return dial(ctx, metadata)
	})
	inbound, client := net.Pipe()
	defer client.Close()
	reporter := newTCPHandshakeInbound(inbound)
	reporter.pending.Store(false)
	parent, cancel := context.WithCancel(context.Background())
	defer cancel()
	finished := make(chan struct{})
	go func() { Tunnel.HandleTCPConnContext(parent, reporter, tcpDialTestMetadata()); close(finished) }()
	<-dialed
	select {
	case <-finished:
		t.Fatal("accepted DROP connection was immediately closed")
	case <-time.After(30 * time.Millisecond):
	}
	cancel()
	select {
	case <-finished:
	case <-time.After(time.Second):
		t.Fatal("DROP ignored existing inbound cancellation")
	}
	if reporter.failure.Load() != 0 || reporter.drops.Load() != 0 || reporter.success.Load() != 0 {
		t.Fatal("accepted DROP emitted a handshake result")
	}
}
