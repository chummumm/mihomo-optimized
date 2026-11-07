//go:build with_gvisor

package tun

import (
	"errors"
	"net"
	"testing"
	"time"
)

func TestDesktopMixedClosesPartiallyStartedSystem(t *testing.T) {
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { listener.Close() })
	accepted := make(chan error, 1)
	go func() {
		connection, err := listener.Accept()
		if connection != nil {
			connection.Close()
		}
		accepted <- err
	}()
	// This is the state after IPv4 succeeds and IPv6 startup fails, before
	// Mixed has created its gVisor stack. The listening socket must still close.
	partial := &Mixed{System: &System{tcpListener: listener}}
	if err := partial.Close(); err != nil {
		t.Fatal(err)
	}
	select {
	case err := <-accepted:
		if !errors.Is(err, net.ErrClosed) {
			t.Fatalf("partial listener was not closed: %v", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("partial stack cleanup left its accept goroutine running")
	}
	if err := (&Mixed{System: &System{}}).Close(); err != nil {
		t.Fatalf("empty system cleanup failed: %v", err)
	}
	if err := (&Mixed{}).Close(); err != nil {
		t.Fatalf("uninitialized stack cleanup failed: %v", err)
	}
}
