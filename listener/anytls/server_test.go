package anytls

import (
	"bytes"
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"sync"
	"syscall"
	"testing"
	"time"

	N "github.com/metacubex/mihomo/common/net"
	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/listener/sing"
	"github.com/metacubex/mihomo/transport/anytls/padding"
	"github.com/metacubex/mihomo/transport/socks5"
)

type anytlsTestTunnel struct {
	C.Tunnel
	handle func(net.Conn, *C.Metadata)
}

func (t anytlsTestTunnel) HandleTCPConn(conn net.Conn, metadata *C.Metadata) {
	t.handle(conn, metadata)
}

type anytlsObservedConn struct {
	net.Conn
	mu     sync.Mutex
	writes bytes.Buffer
}

func (c *anytlsObservedConn) Write(p []byte) (int, error) {
	c.mu.Lock()
	_, _ = c.writes.Write(p)
	c.mu.Unlock()
	return c.Conn.Write(p)
}

func (c *anytlsObservedConn) LocalAddr() net.Addr {
	return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1), Port: 443}
}

func (c *anytlsObservedConn) RemoteAddr() net.Addr {
	return &net.TCPAddr{IP: net.IPv4(127, 0, 0, 2), Port: 50000}
}

func (c *anytlsObservedConn) acknowledgements() ([][]byte, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	var replies [][]byte
	frames := c.writes.Bytes()
	for len(frames) > 0 {
		if len(frames) < 7 {
			return nil, io.ErrUnexpectedEOF
		}
		length := int(binary.BigEndian.Uint16(frames[5:7]))
		if len(frames) < 7+length {
			return nil, io.ErrUnexpectedEOF
		}
		if frames[0] == 7 { // AnyTLS v2 cmdSYNACK
			replies = append(replies, append([]byte(nil), frames[7:7+length]...))
		}
		frames = frames[7+length:]
	}
	return replies, nil
}

func anytlsTestFrame(command byte, id uint32, data []byte) []byte {
	frame := make([]byte, 7+len(data))
	frame[0] = command
	binary.BigEndian.PutUint32(frame[1:5], id)
	binary.BigEndian.PutUint16(frame[5:7], uint16(len(data)))
	copy(frame[7:], data)
	return frame
}

func TestAnyTLSStreamReplyUsesOutboundResult(t *testing.T) {
	for _, test := range []struct {
		name    string
		version int
		dialErr error
	}{
		{"v2 success", 2, nil},
		{"v2 refusal", 2, syscall.ECONNREFUSED},
		{"v1 refusal", 1, syscall.ECONNREFUSED},
	} {
		t.Run(test.name, func(t *testing.T) {
			server, client := net.Pipe()
			observed := &anytlsObservedConn{Conn: server}
			_ = server.SetDeadline(time.Now().Add(5 * time.Second))
			_ = client.SetDeadline(time.Now().Add(5 * time.Second))
			serverDone := make(chan struct{})
			t.Cleanup(func() {
				_ = client.Close()
				_ = server.Close()
				select {
				case <-serverDone:
				case <-time.After(time.Second):
					t.Error("AnyTLS listener did not stop")
				}
			})
			go func() { _, _ = io.Copy(io.Discard, client) }()

			result := make(chan error, 1)
			tunnel := anytlsTestTunnel{handle: func(conn net.Conn, metadata *C.Metadata) {
				before, err := observed.acknowledgements()
				if err != nil || len(before) != 0 {
					result <- fmt.Errorf("SYNACK sent before dialing: replies=%q error=%v", before, err)
					return
				}
				if metadata.DstIP.String() != "192.0.2.1" || metadata.DstPort != 443 {
					result <- fmt.Errorf("wrong destination: %v", metadata)
					return
				}
				if test.dialErr != nil {
					err = N.CloseOnHandshakeFailure(conn, test.dialErr)
					if !errors.Is(err, test.dialErr) {
						result <- fmt.Errorf("lost dial error: %v", err)
						return
					}
				} else if err = N.ReportConnHandshakeSuccess(conn, nil); err != nil {
					result <- err
					return
				}
				after, err := observed.acknowledgements()
				if err != nil {
					result <- err
					return
				}
				if test.version == 1 {
					if len(after) != 0 {
						result <- fmt.Errorf("v2 acknowledgement sent to v1 peer: %q", after)
						return
					}
				} else {
					want := ""
					if test.dialErr != nil {
						want = test.dialErr.Error()
					}
					if len(after) != 1 || string(after[0]) != want {
						result <- fmt.Errorf("wrong SYNACK: got %q, want %q", after, want)
						return
					}
				}
				result <- nil
			}}
			handler, err := sing.NewListenerHandler(sing.ListenerConfig{Tunnel: tunnel, Type: C.ANYTLS})
			if err != nil {
				t.Fatal(err)
			}
			password := sha256.Sum256([]byte("test password"))
			listener := &Listener{userMap: map[[32]byte]string{password: "test user"}}
			factory := padding.NewPaddingFactory([]byte("stop=0"))
			listener.padding.Store(factory)
			go func() {
				listener.HandleConn(observed, handler)
				close(serverDone)
			}()
			request := append(password[:], 0, 0)
			settings := []byte(fmt.Sprintf("v=%d\npadding-md5=%s", test.version, factory.Md5))
			request = append(request, anytlsTestFrame(4, 0, settings)...)
			request = append(request, anytlsTestFrame(1, 1, nil)...)
			request = append(request, anytlsTestFrame(2, 1, socks5.ParseAddr("192.0.2.1:443"))...)
			if _, err := client.Write(request); err != nil {
				t.Fatal(err)
			}
			select {
			case err := <-result:
				if err != nil {
					t.Fatal(err)
				}
			case <-time.After(5 * time.Second):
				t.Fatal("AnyTLS stream did not reach the tunnel")
			}
		})
	}
}
