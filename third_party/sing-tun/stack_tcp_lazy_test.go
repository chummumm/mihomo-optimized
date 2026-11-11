//go:build with_gvisor

package tun

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"net/netip"
	"os"
	"testing"
	"time"

	"github.com/metacubex/gvisor/pkg/buffer"
	"github.com/metacubex/gvisor/pkg/tcpip/header"
	"github.com/metacubex/gvisor/pkg/tcpip/link/channel"
	"github.com/metacubex/gvisor/pkg/tcpip/stack"
	"github.com/metacubex/sing/common/logger"
	M "github.com/metacubex/sing/common/metadata"
	N "github.com/metacubex/sing/common/network"
)

// memoryTCPTun exercises the real forwarders and packet stacks without a host
// TUN device. The mips stack uses Read/Write; gvisor uses a channel endpoint.
type memoryTCPTun struct {
	*memoryTun
	endpoint *channel.Endpoint
}

func (d *memoryTCPTun) NewEndpoint() (stack.LinkEndpoint, stack.NICOptions, error) {
	d.endpoint = channel.New(64, 1500, "")
	go func() {
		for {
			select {
			case raw := <-d.in:
				protocol := header.IPv4ProtocolNumber
				if raw[0]>>4 == 6 {
					protocol = header.IPv6ProtocolNumber
				}
				packet := stack.NewPacketBuffer(stack.PacketBufferOptions{Payload: buffer.MakeWithData(raw)})
				d.endpoint.InjectInbound(protocol, packet)
				packet.DecRef()
			case <-d.done:
				return
			}
		}
	}()
	go func() {
		for {
			packet := d.endpoint.ReadContext(context.Background())
			if packet == nil {
				return
			}
			_, _ = d.WritePacket(packet)
			packet.DecRef()
		}
	}()
	return d.endpoint, stack.NICOptions{}, nil
}

func (d *memoryTCPTun) WritePacket(packet *stack.PacketBuffer) (int, error) {
	view := packet.ToView()
	defer view.Release()
	return d.Write(view.AsSlice())
}

func (d *memoryTCPTun) Close() error {
	_ = d.memoryTun.Close()
	if d.endpoint != nil {
		d.endpoint.Close()
	}
	return nil
}

func newTCPHandshakeStack(t *testing.T, name string, handler *testHandler) (*memoryTCPTun, Stack) {
	t.Helper()
	d := &memoryTCPTun{memoryTun: newMemoryTun()}
	s, err := NewStack(name, StackOptions{
		Context: context.Background(), Tun: d, Handler: handler, Logger: logger.NOP(),
		UDPTimeout: time.Minute, ICMPTimeout: time.Second,
		TunOptions: Options{MTU: 1500,
			Inet4Address: []netip.Prefix{netip.MustParsePrefix("198.18.0.1/30")},
			Inet6Address: []netip.Prefix{netip.MustParsePrefix("fd00::1/126")}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = s.Close(); _ = d.Close() })
	return d, s
}

func awaitTCPResult(t *testing.T, result <-chan error) error {
	t.Helper()
	select {
	case err := <-result:
		return err
	case <-time.After(3 * time.Second):
		t.Fatal("TCP handler did not complete")
		return nil
	}
}

func assertNoTCPPacket(t *testing.T, d *memoryTCPTun) {
	t.Helper()
	select {
	case packet := <-d.out:
		t.Fatalf("SYN must remain unanswered, got %x", packet)
	case <-time.After(30 * time.Millisecond):
	}
}

func tcpHeaderOffset(source netip.Addr) int {
	if source.Is6() {
		return 40
	}
	return 20
}

func acceptTCPHandshake(t *testing.T, d *memoryTCPTun, source, target netip.Addr) uint32 {
	t.Helper()
	synAck := readPacket(t, d.memoryTun)
	offset := tcpHeaderOffset(source)
	if synAck[offset+13]&18 != 18 {
		t.Fatalf("expected SYN ACK, got %x", synAck)
	}
	ack := binary.BigEndian.Uint32(synAck[offset+4:]) + 1
	d.in <- tcpPacket(source, target, 101, ack, 16, nil)
	return ack
}

func TestTCPDeferredHandshake(t *testing.T) {
	for _, name := range []string{"mips", "gvisor"} {
		for _, pair := range [][2]string{{"198.18.0.1", "8.8.8.8"}, {"fd00::1", "2001:4860:4860::8888"}} {
			t.Run(name+"/"+pair[0], func(t *testing.T) {
				source, target := netip.MustParseAddr(pair[0]), netip.MustParseAddr(pair[1])
				entered, proceed := make(chan net.Conn, 1), make(chan struct{})
				result := make(chan error, 1)
				h := &testHandler{tcp: func(ctx context.Context, conn net.Conn, metadata M.Metadata) error {
					defer conn.Close()
					entered <- conn
					select {
					case <-proceed:
					case <-ctx.Done():
						return ctx.Err()
					}
					if err := N.ReportHandshakeSuccess(conn); err != nil {
						result <- err
						return err
					}
					if ctx.Err() != nil {
						result <- errors.New("accepted connection inherited request cancellation")
						return nil
					}
					request, err := io.ReadAll(conn)
					if err == nil && (!bytes.Equal(request, []byte("hello")) || metadata.Source.Addr != source || metadata.Destination.Addr != target) {
						err = errors.New("wrong TCP payload or metadata")
					}
					if err == nil {
						_, err = conn.Write([]byte("response after FIN"))
					}
					if err == nil {
						err = conn.(interface{ CloseWrite() error }).CloseWrite()
					}
					result <- err
					return err
				}}
				d, _ := newTCPHandshakeStack(t, name, h)
				d.in <- tcpPacket(source, target, 100, 0, 2, nil)
				var conn net.Conn
				select {
				case conn = <-entered:
				case <-time.After(3 * time.Second):
					t.Fatal("handler did not receive unaccepted SYN")
				}
				if !conn.(N.EarlyConn).NeedHandshake() {
					t.Fatal("new TCP request is already accepted")
				}
				if conn.LocalAddr().String() != netip.AddrPortFrom(target, 443).String() || conn.RemoteAddr().String() != netip.AddrPortFrom(source, 12345).String() {
					t.Fatalf("wrong lazy addresses: %s / %s", conn.LocalAddr(), conn.RemoteAddr())
				}
				_ = conn.SetDeadline(time.Now().Add(2 * time.Second))
				assertNoTCPPacket(t, d)
				close(proceed)
				ack := acceptTCPHandshake(t, d, source, target)
				d.in <- tcpPacket(source, target, 101, ack, 25, []byte("hello")) // request plus FIN
				var response []byte
				sawFIN := false
				for !sawFIN || len(response) < len("response after FIN") {
					packet := readPacket(t, d.memoryTun)
					offset := tcpHeaderOffset(source)
					flags := packet[offset+13]
					if flags&4 != 0 {
						t.Fatalf("normal half-close produced RST: %x", packet)
					}
					payload := packet[offset+int(packet[offset+12]>>4)*4:]
					response = append(response, payload...)
					sawFIN = sawFIN || flags&1 != 0
					sequence := binary.BigEndian.Uint32(packet[offset+4:]) + uint32(len(payload))
					if flags&1 != 0 {
						sequence++
					}
					d.in <- tcpPacket(source, target, 107, sequence, 16, nil)
				}
				if string(response) != "response after FIN" {
					t.Fatalf("wrong response: %q", response)
				}
				if err := awaitTCPResult(t, result); err != nil {
					t.Fatal(err)
				}
			})
		}
	}
}

func TestTCPHandshakeFailureAndDrop(t *testing.T) {
	for _, name := range []string{"mips", "gvisor"} {
		for _, action := range []string{"reject", "drop", "close"} {
			t.Run(name+"/"+action, func(t *testing.T) {
				result := make(chan error, 1)
				h := &testHandler{tcp: func(_ context.Context, conn net.Conn, _ M.Metadata) error {
					defer conn.Close()
					var err error
					switch action {
					case "reject":
						err = conn.(N.HandshakeFailure).HandshakeFailure(errors.New("outbound refused"))
					case "drop":
						err = conn.(interface{ HandshakeDrop() error }).HandshakeDrop()
					case "close":
						err = conn.Close()
					}
					result <- err
					return nil
				}}
				d, _ := newTCPHandshakeStack(t, name, h)
				d.in <- tcpPacket(netip.MustParseAddr("198.18.0.1"), netip.MustParseAddr("8.8.8.8"), 100, 0, 2, nil)
				if err := awaitTCPResult(t, result); err != nil {
					t.Fatal(err)
				}
				if action == "drop" {
					assertNoTCPPacket(t, d)
				} else {
					packet := readPacket(t, d.memoryTun)
					if packet[33]&4 == 0 || packet[33]&2 != 0 {
						t.Fatalf("expected SYN rejection without SYN ACK: %x", packet)
					}
				}
			})
		}
	}
}

func TestTCPReadAcceptsForSniffing(t *testing.T) {
	for _, name := range []string{"mips", "gvisor"} {
		t.Run(name, func(t *testing.T) {
			result := make(chan error, 1)
			h := &testHandler{tcp: func(_ context.Context, conn net.Conn, _ M.Metadata) error {
				defer conn.Close()
				_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
				data := make([]byte, 5)
				_, err := io.ReadFull(conn, data)
				if err == nil && (string(data) != "hello" || conn.(N.EarlyConn).NeedHandshake()) {
					err = errors.New("sniffer read did not accept the connection")
				}
				if err == nil {
					err = conn.(N.HandshakeFailure).HandshakeFailure(errors.New("routing failed after sniffing"))
				}
				result <- err
				return nil
			}}
			d, _ := newTCPHandshakeStack(t, name, h)
			source, target := netip.MustParseAddr("198.18.0.1"), netip.MustParseAddr("8.8.8.8")
			d.in <- tcpPacket(source, target, 100, 0, 2, nil)
			ack := acceptTCPHandshake(t, d, source, target)
			d.in <- tcpPacket(source, target, 101, ack, 24, []byte("hello"))
			if err := awaitTCPResult(t, result); err != nil {
				t.Fatal(err)
			}
			for {
				packet := readPacket(t, d.memoryTun)
				if packet[33]&4 != 0 {
					break
				}
				if packet[33]&1 != 0 && name != "mips" {
					t.Fatalf("post-sniff failure produced FIN instead of RST: %x", packet)
				}
				// The pinned mipstack Close may queue FIN before its abort is
				// published, even with SetLinger(0). Require the eventual RST;
				// pending rejection is separately required to send only RST.
			}
		})
	}
}

func TestTCPPendingHandshakeClose(t *testing.T) {
	for _, name := range []string{"mips", "gvisor"} {
		t.Run(name, func(t *testing.T) {
			entered := make(chan struct{})
			result := make(chan error, 1)
			h := &testHandler{tcp: func(ctx context.Context, conn net.Conn, _ M.Metadata) error {
				close(entered)
				<-ctx.Done()
				result <- conn.Close()
				return nil
			}}
			d, s := newTCPHandshakeStack(t, name, h)
			d.in <- tcpPacket(netip.MustParseAddr("198.18.0.1"), netip.MustParseAddr("8.8.8.8"), 100, 0, 2, nil)
			select {
			case <-entered:
			case <-time.After(3 * time.Second):
				t.Fatal("handler did not start")
			}
			if err := s.Close(); err != nil {
				t.Fatal(err)
			}
			if err := awaitTCPResult(t, result); err != nil {
				t.Fatal(err)
			}
		})
	}
}

func TestTCPHandshakeDeadline(t *testing.T) {
	for _, name := range []string{"mips", "gvisor"} {
		for _, operation := range []string{"read", "success"} {
			t.Run(name+"/"+operation, func(t *testing.T) {
				result := make(chan error, 1)
				h := &testHandler{tcp: func(_ context.Context, conn net.Conn, _ M.Metadata) error {
					defer conn.Close()
					_ = conn.SetDeadline(time.Now().Add(40 * time.Millisecond))
					var err error
					if operation == "read" {
						_, err = conn.Read(make([]byte, 1))
					} else {
						err = N.ReportHandshakeSuccess(conn)
					}
					result <- err
					return nil
				}}
				d, _ := newTCPHandshakeStack(t, name, h)
				d.in <- tcpPacket(netip.MustParseAddr("198.18.0.1"), netip.MustParseAddr("8.8.8.8"), 100, 0, 2, nil)
				// Deliberately withhold the final ACK of the local handshake.
				if err := awaitTCPResult(t, result); !errors.Is(err, os.ErrDeadlineExceeded) {
					t.Fatalf("handshake ignored deadline: %v", err)
				}
			})
		}
	}
}

var _ GVisorTun = (*memoryTCPTun)(nil)
