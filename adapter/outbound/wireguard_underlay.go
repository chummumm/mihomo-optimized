package outbound

import (
	"context"
	"net"
	"net/netip"

	C "github.com/metacubex/mihomo/constant"
)

// Native WireGuard keeps its bind for the instance lifetime. Each attempt to
// establish that shared carrier still needs a finite deadline, including when
// dialer-proxy itself performs asynchronous initialization or a UDP handshake.
// This deadline belongs to carrier creation, not to its established traffic.
type wireGuardUnderlayDialer struct{ C.Dialer }

func (d wireGuardUnderlayDialer) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	ctx, cancel := context.WithTimeout(ctx, C.DefaultTCPTimeout)
	defer cancel()
	return d.Dialer.DialContext(ctx, network, address)
}

func (d wireGuardUnderlayDialer) ListenPacket(ctx context.Context, network, address string, target netip.AddrPort) (net.PacketConn, error) {
	ctx, cancel := context.WithTimeout(ctx, C.DefaultTCPTimeout)
	defer cancel()
	return d.Dialer.ListenPacket(ctx, network, address, target)
}
