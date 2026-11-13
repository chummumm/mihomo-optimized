package anytls

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"io"
	"net"
	"sync"
	"testing"
	"time"

	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/listener/sing"
	"github.com/metacubex/mihomo/transport/anytls/padding"
	"github.com/metacubex/mihomo/transport/socks5"
)

// The initial Read contains authentication and coalesced session frames. The
// next Read reaches the transport only after the session has consumed them.
type anytlsCloseDuringReadConn struct {
	*anytlsObservedConn
	reads          int // HandleConn and its synchronous receive loop are one reader.
	readEntered    chan struct{}
	transportClose chan struct{}
	closeOnce      sync.Once
	finError       error
}

func (c *anytlsCloseDuringReadConn) Read(p []byte) (int, error) {
	c.reads++
	if c.reads == 2 {
		close(c.readEntered)
	}
	return c.anytlsObservedConn.Read(p)
}

func (c *anytlsCloseDuringReadConn) Write(p []byte) (int, error) {
	if len(p) > 0 && p[0] == 3 { // cmdFIN: a stream's failed control write closes the session.
		return 0, c.finError
	}
	return c.anytlsObservedConn.Write(p)
}

func (c *anytlsCloseDuringReadConn) SetDeadline(time.Time) error {
	// Session.Close first attempts SetDeadline. Make it fail without touching
	// the transport so only Close can unblock Read; a lock spanning Read would
	// deadlock here instead of being rescued by that deadline.
	return errors.ErrUnsupported
}

func (c *anytlsCloseDuringReadConn) Close() error {
	err := c.anytlsObservedConn.Close()
	c.closeOnce.Do(func() { close(c.transportClose) })
	return err
}

func TestAnyTLSStreamCloseUnblocksSessionRead(t *testing.T) {
	server, client := net.Pipe()
	// Bound only the fixture's initial write. The server has no read deadline:
	// Close must wake it, and only failure cleanup closes the raw pipe directly.
	_ = client.SetWriteDeadline(time.Now().Add(10 * time.Second))
	finError := errors.New("fixture FIN write failed")
	transport := &anytlsCloseDuringReadConn{
		anytlsObservedConn: &anytlsObservedConn{Conn: server},
		readEntered:        make(chan struct{}),
		transportClose:     make(chan struct{}),
		finError:           finError,
	}
	serverDone := make(chan struct{})
	clientDone := make(chan struct{})
	t.Cleanup(func() {
		_ = client.Close()
		_ = server.Close()
		for _, done := range []<-chan struct{}{serverDone, clientDone} {
			select {
			case <-done:
			case <-time.After(time.Second):
				t.Error("AnyTLS fixture did not stop after raw transport cleanup")
			}
		}
	})
	go func() {
		_, _ = io.Copy(io.Discard, client)
		close(clientDone)
	}()

	result := make(chan error, 1)
	tunnel := anytlsTestTunnel{handle: func(conn net.Conn, metadata *C.Metadata) {
		if metadata.DstIP.String() != "192.0.2.1" || metadata.DstPort != 443 {
			result <- fmt.Errorf("coalesced destination changed: %v", metadata)
			return
		}
		select {
		case <-transport.readEntered:
		case <-time.After(2 * time.Second):
			result <- errors.New("session did not consume its cached frames and enter transport Read")
			return
		}
		result <- conn.Close()
	}}
	handler, err := sing.NewListenerHandler(sing.ListenerConfig{Tunnel: tunnel, Type: C.ANYTLS})
	if err != nil {
		t.Fatal(err)
	}
	password := sha256.Sum256([]byte("concurrent close test password"))
	listener := &Listener{userMap: map[[32]byte]string{password: "test user"}}
	factory := padding.NewPaddingFactory([]byte("stop=0"))
	listener.padding.Store(factory)
	go func() {
		listener.HandleConn(transport, handler)
		close(serverDone)
	}()
	// The prefix handed to the session must exclude authentication and padding.
	request := append(password[:], 0, 3, 0xa5, 0x5a, 0xa5)
	settings := []byte(fmt.Sprintf("v=2\npadding-md5=%s", factory.Md5))
	request = append(request, anytlsTestFrame(4, 0, settings)...)
	request = append(request, anytlsTestFrame(1, 1, nil)...)
	request = append(request, anytlsTestFrame(2, 1, socks5.ParseAddr("192.0.2.1:443"))...)
	if _, err := client.Write(request); err != nil {
		t.Fatal(err)
	}
	select {
	case err := <-result:
		if !errors.Is(err, finError) {
			t.Fatalf("stream did not return its failed control write: %v", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("stream/session Close blocked behind transport Read")
	}
	select {
	case <-transport.transportClose:
	case <-time.After(time.Second):
		t.Fatal("session did not close the underlying transport")
	}
	select {
	case <-serverDone:
	case <-time.After(time.Second):
		t.Fatal("transport Close did not unblock the session receive loop")
	}
}
