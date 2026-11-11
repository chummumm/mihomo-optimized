package sing

import (
	"context"
	"errors"
	"io"
	"net"
	"testing"
	"time"

	C "github.com/metacubex/mihomo/constant"

	mux "github.com/metacubex/sing-mux"
	vmess "github.com/metacubex/sing-vmess"
	M "github.com/metacubex/sing/common/metadata"
	"github.com/metacubex/sing/common/uot"
)

type handshakeTestConn struct {
	net.Conn
	reported int
	read     bool
	err      error
}

func (c *handshakeTestConn) HandshakeSuccess() error         { c.reported++; return c.err }
func (c *handshakeTestConn) Read([]byte) (int, error)        { c.read = true; return 0, io.EOF }
func (c *handshakeTestConn) Close() error                    { return nil }
func (c *handshakeTestConn) LocalAddr() net.Addr             { return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)} }
func (c *handshakeTestConn) RemoteAddr() net.Addr            { return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 2)} }
func (c *handshakeTestConn) SetReadDeadline(time.Time) error { return nil }

type handshakeTestTunnel struct {
	C.Tunnel
	handle func(net.Conn, *C.Metadata)
}

func (t handshakeTestTunnel) HandleTCPConn(conn net.Conn, metadata *C.Metadata) {
	t.handle(conn, metadata)
}

func TestHandshakeSpecialDestinationsAcknowledgeBeforeLocalService(t *testing.T) {
	for _, destination := range []string{mux.Destination.Fqdn, vmess.MuxDestination.Fqdn, uot.MagicAddress, uot.LegacyMagicAddress} {
		t.Run(destination, func(t *testing.T) {
			wantErr := errors.New("cannot send acknowledgement")
			conn := &handshakeTestConn{err: wantErr}
			h := &ListenerHandler{}
			err := h.NewConnection(context.Background(), conn, M.Metadata{Destination: M.Socksaddr{Fqdn: destination}})
			if !errors.Is(err, wantErr) || conn.reported != 1 || conn.read {
				t.Fatalf("special service bypassed its handshake: err=%v reports=%d read=%v", err, conn.reported, conn.read)
			}
		})
	}
}

func TestOrdinaryDestinationWaitsForTunnelHandshake(t *testing.T) {
	conn := &handshakeTestConn{}
	called := false
	h := &ListenerHandler{ListenerConfig: ListenerConfig{Tunnel: handshakeTestTunnel{handle: func(net.Conn, *C.Metadata) {
		called = true
		if conn.reported != 0 {
			t.Fatal("ordinary TCP request was acknowledged before dialing")
		}
	}}}}
	if err := h.NewConnection(context.Background(), conn, M.Metadata{Destination: M.ParseSocksaddr("192.0.2.1:443")}); err != nil {
		t.Fatal(err)
	}
	if !called {
		t.Fatal("ordinary TCP request did not reach the tunnel")
	}
}
