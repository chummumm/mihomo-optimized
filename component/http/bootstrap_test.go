package http

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"testing"
	"time"
)

type blockingBootstrapDialer struct{}

func (blockingBootstrapDialer) DialContext(ctx context.Context, _, _ string) (net.Conn, error) {
	<-ctx.Done()
	return nil, ctx.Err()
}
func (blockingBootstrapDialer) ListenPacket(context.Context, string, string, netip.AddrPort) (net.PacketConn, error) {
	panic("unexpected UDP")
}

func TestResourceBootstrapConnectDeadline(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	started := time.Now()
	_, err := HttpRequest(ctx, "http://bootstrap.invalid/resource", "GET", nil, nil, WithDialer(blockingBootstrapDialer{}), WithDialTimeout(25*time.Millisecond))
	if !errors.Is(err, context.DeadlineExceeded) || time.Since(started) > 500*time.Millisecond {
		t.Fatalf("connect deadline not applied: %s %v", time.Since(started), err)
	}
}
