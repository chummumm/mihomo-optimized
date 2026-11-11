package net_test

import (
	"errors"
	"io"
	"net"
	"reflect"
	"syscall"
	"testing"
	"time"

	N "github.com/metacubex/mihomo/common/net"
)

type handshakeReporter struct {
	net.Conn
	events []string
	cause  error
}

func (c *handshakeReporter) HandshakeFailure(err error) error {
	c.events = append(c.events, "failure")
	c.cause = err
	_, writeErr := c.Conn.Write([]byte("failure"))
	return writeErr
}

func (c *handshakeReporter) SetLinger(int) error {
	c.events = append(c.events, "linger")
	return nil
}

func (c *handshakeReporter) Close() error {
	c.events = append(c.events, "close")
	return c.Conn.Close()
}

func TestHandshakeFailureReplyPrecedesClose(t *testing.T) {
	inbound, peer := net.Pipe()
	defer peer.Close()
	_ = peer.SetDeadline(time.Now().Add(time.Second))
	reporter := &handshakeReporter{Conn: inbound}
	finished := make(chan error, 1)
	go func() { finished <- N.CloseOnHandshakeFailure(N.NewBufferedConn(reporter), syscall.ECONNREFUSED) }()
	response, err := io.ReadAll(peer)
	if err != nil || string(response) != "failure" {
		t.Fatalf("failure reply was lost before close: %q, %v", response, err)
	}
	if err = <-finished; !errors.Is(err, syscall.ECONNREFUSED) {
		t.Fatalf("original failure lost: %v", err)
	}
	if !reflect.DeepEqual(reporter.events, []string{"failure", "close"}) {
		t.Fatalf("wrong reply/close order: %v", reporter.events)
	}
}

func tcpPair(t *testing.T) (*net.TCPConn, *net.TCPConn) {
	t.Helper()
	listener, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	peer, err := net.DialTCP("tcp4", nil, listener.Addr().(*net.TCPAddr))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = peer.Close() })
	conn, err := listener.AcceptTCP()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = conn.Close() })
	_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
	_ = peer.SetDeadline(time.Now().Add(3 * time.Second))
	return conn, peer
}

func TestHandshakeFailureAcceptedTCPResetsBeforeClose(t *testing.T) {
	inbound, peer := tcpPair(t)
	_ = N.CloseOnHandshakeFailure(N.NewBufferedConn(inbound), syscall.ECONNREFUSED)
	_, err := peer.Read(make([]byte, 1))
	if !errors.Is(err, syscall.ECONNRESET) {
		t.Fatalf("accepted TCP failure became EOF instead of reset: %v", err)
	}
}

func TestRelayPreservesTCPHalfCloseAndReverseDrain(t *testing.T) {
	left, client := tcpPair(t)
	right, server := tcpPair(t)
	done := make(chan struct{})
	go func() { N.Relay(N.NewBufferedConn(left), N.NewBufferedConn(right)); close(done) }()
	_, _ = client.Write([]byte("request"))
	_ = client.CloseWrite()
	request, err := io.ReadAll(server)
	if err != nil || string(request) != "request" {
		t.Fatalf("request half-close: %q, %v", request, err)
	}
	_, _ = server.Write([]byte("response after request EOF"))
	_ = server.CloseWrite()
	response, err := io.ReadAll(client)
	if err != nil || string(response) != "response after request EOF" {
		t.Fatalf("reverse data was truncated: %q, %v", response, err)
	}
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("relay did not finish after both half-closes")
	}
}

type readFailureConn struct{ net.Conn }

func (readFailureConn) Read([]byte) (int, error) { return 0, syscall.ECONNRESET }

func TestRelayReadErrorClosesBothDirections(t *testing.T) {
	left, client := net.Pipe()
	right, server := net.Pipe()
	defer client.Close()
	defer server.Close()
	done := make(chan struct{})
	go func() { N.Relay(readFailureConn{left}, right); close(done) }()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("opposite copy remained blocked after a read failure")
	}
}
