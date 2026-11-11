package socks

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"syscall"
	"testing"
	"time"

	N "github.com/metacubex/mihomo/common/net"
	"github.com/metacubex/mihomo/component/auth"
	C "github.com/metacubex/mihomo/constant"
	authStore "github.com/metacubex/mihomo/listener/auth"
	"github.com/metacubex/mihomo/transport/socks4"
	"github.com/metacubex/mihomo/transport/socks5"

	"github.com/metacubex/sing/common/network"
)

type socksTestConn struct {
	input    *bytes.Reader
	output   bytes.Buffer
	local    net.Addr
	closed   bool
	writeErr error
}

func newSocksTestConn(input []byte) *socksTestConn {
	return &socksTestConn{input: bytes.NewReader(input), local: &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 1080}}
}

func (c *socksTestConn) Read(p []byte) (int, error) { return c.input.Read(p) }
func (c *socksTestConn) Write(p []byte) (int, error) {
	if c.writeErr != nil {
		return 0, c.writeErr
	}
	return c.output.Write(p)
}
func (c *socksTestConn) Close() error        { c.closed = true; return nil }
func (c *socksTestConn) LocalAddr() net.Addr { return c.local }
func (c *socksTestConn) RemoteAddr() net.Addr {
	return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 2), Port: 50000}
}
func (c *socksTestConn) SetDeadline(time.Time) error      { return nil }
func (c *socksTestConn) SetReadDeadline(time.Time) error  { return nil }
func (c *socksTestConn) SetWriteDeadline(time.Time) error { return nil }

type socksTestTunnel struct {
	C.Tunnel
	handle func(net.Conn, *C.Metadata)
}

func (t socksTestTunnel) HandleTCPConn(conn net.Conn, metadata *C.Metadata) {
	t.handle(conn, metadata)
}

func socksConnectRequest(version byte) []byte {
	if version == socks4.Version {
		return []byte{4, 1, 0, 80, 192, 0, 2, 1, 0}
	}
	return []byte{5, 1, 0, 5, 1, 0, 1, 192, 0, 2, 1, 0, 80}
}

func socksAuthReply(version byte) []byte {
	if version == socks5.Version {
		return []byte{5, 0}
	}
	return nil
}

func serveSocks(version byte, conn net.Conn, tunnel C.Tunnel) {
	if version == socks4.Version {
		HandleSocks4(conn, tunnel, authStore.Nil)
	} else {
		HandleSocks5(conn, tunnel, authStore.Nil)
	}
}

func TestSOCKSConnectReplyWaitsForOutbound(t *testing.T) {
	for _, version := range []byte{socks4.Version, socks5.Version} {
		t.Run(fmt.Sprint(version), func(t *testing.T) {
			wire := newSocksTestConn(socksConnectRequest(version))
			called := false
			serveSocks(version, wire, socksTestTunnel{handle: func(conn net.Conn, metadata *C.Metadata) {
				called = true
				if !bytes.Equal(wire.output.Bytes(), socksAuthReply(version)) {
					t.Fatalf("CONNECT was acknowledged before dialing: %x", wire.output.Bytes())
				}
				if !N.NeedHandshake(conn) || network.UnwrapReader(conn) != conn || network.UnwrapWriter(conn) != conn {
					t.Fatal("pending CONNECT can be bypassed by an optimized copy")
				}
				if metadata.DstIP.String() != "192.0.2.1" || metadata.DstPort != 80 {
					t.Fatalf("unexpected destination: %+v", metadata)
				}
				remote := newSocksTestConn(nil)
				remote.local = &net.TCPAddr{IP: net.IPv4(127, 0, 0, 3), Port: 4242}
				if err := N.ReportConnHandshakeSuccess(conn, remote); err != nil {
					t.Fatal(err)
				}
				if N.NeedHandshake(conn) || network.UnwrapReader(conn) != wire || network.UnwrapWriter(conn) != wire {
					t.Fatal("completed CONNECT did not release the copy wrapper")
				}
				if err := conn.(*lazyConn).HandshakeFailure(syscall.ECONNREFUSED); !errors.Is(err, os.ErrInvalid) {
					t.Fatalf("second response was not rejected: %v", err)
				}
				if _, err := conn.Write([]byte("reply")); err != nil {
					t.Fatal(err)
				}
			}})
			if !called {
				t.Fatal("request did not reach the tunnel")
			}
			want := append([]byte(nil), socksAuthReply(version)...)
			if version == socks4.Version {
				want = append(want, 0, 90, 0x10, 0x92, 127, 0, 0, 3)
			} else {
				want = append(want, 5, 0, 0, 1, 127, 0, 0, 3, 0x10, 0x92)
			}
			want = append(want, "reply"...)
			if !bytes.Equal(wire.output.Bytes(), want) {
				t.Fatalf("reply did not use the outbound bound address, or was duplicated:\n got %x\nwant %x", wire.output.Bytes(), want)
			}
		})
	}
}

func TestSOCKSConnectFailureRepliesBeforeClose(t *testing.T) {
	for _, version := range []byte{socks4.Version, socks5.Version} {
		t.Run(fmt.Sprint(version), func(t *testing.T) {
			wire := newSocksTestConn(socksConnectRequest(version))
			serveSocks(version, wire, socksTestTunnel{handle: func(conn net.Conn, _ *C.Metadata) {
				err := N.CloseOnHandshakeFailure(conn, fmt.Errorf("dial target: %w", syscall.ECONNREFUSED))
				if !errors.Is(err, syscall.ECONNREFUSED) {
					t.Fatalf("original dial error was lost: %v", err)
				}
				if !wire.closed {
					t.Fatal("failed CONNECT was not closed")
				}
				if _, err := conn.Write([]byte("unexpected")); !errors.Is(err, os.ErrInvalid) {
					t.Fatalf("failed CONNECT allowed application writes: %v", err)
				}
			}})
			want := append([]byte(nil), socksAuthReply(version)...)
			if version == socks4.Version {
				want = append(want, 0, 91, 0, 0, 0, 0, 0, 0)
			} else {
				want = append(want, 5, 5, 0, 1, 0, 0, 0, 0, 0, 0)
			}
			if !bytes.Equal(wire.output.Bytes(), want) {
				t.Fatalf("wrong failure response: got %x, want %x", wire.output.Bytes(), want)
			}
		})
	}
}

func TestSOCKSConnectReadForSniffingMayReplyEarly(t *testing.T) {
	for _, version := range []byte{socks4.Version, socks5.Version} {
		t.Run(fmt.Sprint(version), func(t *testing.T) {
			request := append(socksConnectRequest(version), "GET"...)
			wire := newSocksTestConn(request)
			serveSocks(version, wire, socksTestTunnel{handle: func(conn net.Conn, _ *C.Metadata) {
				var payload [3]byte
				if _, err := io.ReadFull(conn, payload[:]); err != nil || string(payload[:]) != "GET" {
					t.Fatalf("sniff read: %q, %v", payload[:], err)
				}
				if N.NeedHandshake(conn) {
					t.Fatal("actual application read did not finish the handshake")
				}
				reply := append([]byte(nil), wire.output.Bytes()...)
				if err := N.ReportConnHandshakeSuccess(conn, nil); err != nil {
					t.Fatal(err)
				}
				if !bytes.Equal(wire.output.Bytes(), reply) {
					t.Fatal("success callback duplicated the sniff-triggered reply")
				}
			}})
		})
	}
}

func TestSOCKSConnectReplyWriteFailureIsNotRetried(t *testing.T) {
	wire := newSocksTestConn(nil)
	wire.writeErr = io.ErrClosedPipe
	conn := newLazyConn(wire, socks5.Version)
	if err := conn.HandshakeSuccess(); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatal(err)
	}
	wire.writeErr = nil
	if _, err := conn.Write([]byte("payload")); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatalf("failed reply was retried: %v", err)
	}
	if wire.output.Len() != 0 || conn.ReaderReplaceable() || conn.WriterReplaceable() {
		t.Fatal("failed handshake was bypassed")
	}
}

func TestSOCKSUDPAssociateKeepsImmediateControlReply(t *testing.T) {
	request := socksConnectRequest(socks5.Version)
	request[4] = socks5.CmdUDPAssociate
	wire := newSocksTestConn(append(request, "control data"...))
	HandleSocks5(wire, socksTestTunnel{handle: func(net.Conn, *C.Metadata) {
		t.Fatal("UDP control connection was routed as TCP")
	}}, authStore.Nil)
	want := []byte{5, 0, 5, 0, 0, 1, 127, 0, 0, 1, 4, 56}
	if !bytes.Equal(wire.output.Bytes(), want) || !wire.closed || wire.input.Len() != 0 {
		t.Fatalf("UDP control behavior changed: reply=%x closed=%v unread=%d", wire.output.Bytes(), wire.closed, wire.input.Len())
	}
}

func TestSOCKSAuthenticationFailureIsImmediate(t *testing.T) {
	store := authStore.NewAuthStore(auth.NewAuthenticator([]auth.AuthUser{{User: "user", Pass: "correct"}}))
	wire := newSocksTestConn([]byte{5, 1, 2, 1, 4, 'u', 's', 'e', 'r', 5, 'w', 'r', 'o', 'n', 'g'})
	HandleSocks5(wire, socksTestTunnel{handle: func(net.Conn, *C.Metadata) {
		t.Fatal("invalid credentials reached the tunnel")
	}}, store)
	if !bytes.Equal(wire.output.Bytes(), []byte{5, 2, 1, 1}) || !wire.closed {
		t.Fatalf("authentication failure was deferred: %x", wire.output.Bytes())
	}
}

func TestSOCKSAuthenticationSuccessDoesNotGrantConnect(t *testing.T) {
	store := authStore.NewAuthStore(auth.NewAuthenticator([]auth.AuthUser{{User: "user", Pass: "pass"}}))
	request := []byte{5, 1, 2, 1, 4, 'u', 's', 'e', 'r', 4, 'p', 'a', 's', 's'}
	request = append(request, socksConnectRequest(socks5.Version)[3:]...)
	wire := newSocksTestConn(request)
	called := false
	HandleSocks5(wire, socksTestTunnel{handle: func(conn net.Conn, metadata *C.Metadata) {
		called = true
		if !bytes.Equal(wire.output.Bytes(), []byte{5, 2, 1, 0}) || !N.NeedHandshake(conn) || metadata.InUser != "user" {
			t.Fatalf("authentication altered CONNECT state: reply=%x metadata=%+v", wire.output.Bytes(), metadata)
		}
	}}, store)
	if !called {
		t.Fatal("authenticated CONNECT did not reach the tunnel")
	}
}
