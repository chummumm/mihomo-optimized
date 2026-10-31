package outbound

import (
	"context"
	"net"
	"net/netip"
	"sync"

	C "github.com/metacubex/mihomo/constant"
	wireguard "github.com/metacubex/sing-wireguard"
	wgconn "github.com/metacubex/wireguard-go/conn"
)

// Closing the carrier outside native Device.Close must also serialize with an
// asynchronous EventUp. Once the instance is canceled, a late Open may not
// revive its bind. Normal native rebinds remain possible while it is running.
type wireGuardClientBind struct {
	*wireguard.ClientBind
	ctx context.Context
	mu  sync.Mutex
}

func (b *wireGuardClientBind) Open(port uint16) ([]wgconn.ReceiveFunc, uint16, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if err := b.ctx.Err(); err != nil {
		return nil, 0, err
	}
	return b.ClientBind.Open(port)
}

func (b *wireGuardClientBind) Close() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.ClientBind.Close()
}

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
