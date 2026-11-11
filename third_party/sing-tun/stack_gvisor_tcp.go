//go:build with_gvisor

package tun

import (
	"context"
	"net"
	"net/netip"
	"sync"
	"time"

	"github.com/metacubex/gvisor/pkg/tcpip"
	"github.com/metacubex/gvisor/pkg/tcpip/adapters/gonet"
	"github.com/metacubex/gvisor/pkg/tcpip/header"
	"github.com/metacubex/gvisor/pkg/tcpip/stack"
	"github.com/metacubex/gvisor/pkg/tcpip/transport/tcp"
	"github.com/metacubex/gvisor/pkg/waiter"
	"github.com/metacubex/sing-tun/internal/gtcpip/checksum"
	"github.com/metacubex/sing/common"
	M "github.com/metacubex/sing/common/metadata"
)

type TCPForwarder struct {
	ctx                  context.Context
	stack                *stack.Stack
	handler              Handler
	inet4LoopbackAddress []tcpip.Address
	inet6LoopbackAddress []tcpip.Address
	tun                  GVisorTun
	forwarder            *tcp.Forwarder
	done                 chan struct{}
	closeOnce            sync.Once
}

func NewTCPForwarder(ctx context.Context, stack *stack.Stack, handler Handler) *TCPForwarder {
	return NewTCPForwarderWithLoopback(ctx, stack, handler, nil, nil, nil)
}

func NewTCPForwarderWithLoopback(ctx context.Context, stack *stack.Stack, handler Handler, inet4LoopbackAddress []netip.Addr, inet6LoopbackAddress []netip.Addr, tun GVisorTun) *TCPForwarder {
	forwarder := &TCPForwarder{
		ctx:                  ctx,
		stack:                stack,
		handler:              handler,
		inet4LoopbackAddress: common.Map(inet4LoopbackAddress, AddressFromAddr),
		inet6LoopbackAddress: common.Map(inet6LoopbackAddress, AddressFromAddr),
		tun:                  tun,
		done:                 make(chan struct{}),
	}
	forwarder.forwarder = tcp.NewForwarder(stack, 0, 1024, forwarder.Forward)
	return forwarder
}

func (f *TCPForwarder) Close() {
	f.closeOnce.Do(func() { close(f.done) })
}

func (f *TCPForwarder) HandlePacket(id stack.TransportEndpointID, pkt *stack.PacketBuffer) bool {
	for _, inet4LoopbackAddress := range f.inet4LoopbackAddress {
		if id.LocalAddress == inet4LoopbackAddress {
			ipHdr := pkt.Network().(header.IPv4)
			ipHdr.SetDestinationAddressWithChecksumUpdate(ipHdr.SourceAddress())
			ipHdr.SetSourceAddressWithChecksumUpdate(inet4LoopbackAddress)
			tcpHdr := header.TCP(pkt.TransportHeader().Slice())
			tcpHdr.SetChecksum(0)
			tcpHdr.SetChecksum(^checksum.Combine(pkt.Data().Checksum(), tcpHdr.CalculateChecksum(
				header.PseudoHeaderChecksum(header.TCPProtocolNumber, ipHdr.SourceAddress(), ipHdr.DestinationAddress(), ipHdr.PayloadLength()),
			)))
			f.tun.WritePacket(pkt)
			return true
		}
	}
	for _, inet6LoopbackAddress := range f.inet6LoopbackAddress {
		if id.LocalAddress == inet6LoopbackAddress {
			ipHdr := pkt.Network().(header.IPv6)
			ipHdr.SetDestinationAddress(ipHdr.SourceAddress())
			ipHdr.SetSourceAddress(inet6LoopbackAddress)
			tcpHdr := header.TCP(pkt.TransportHeader().Slice())
			tcpHdr.SetChecksum(0)
			tcpHdr.SetChecksum(^checksum.Combine(pkt.Data().Checksum(), tcpHdr.CalculateChecksum(
				header.PseudoHeaderChecksum(header.TCPProtocolNumber, ipHdr.SourceAddress(), ipHdr.DestinationAddress(), ipHdr.PayloadLength()),
			)))
			f.tun.WritePacket(pkt)
			return true
		}
	}
	return f.forwarder.HandlePacket(id, pkt)
}

func (f *TCPForwarder) Forward(r *tcp.ForwarderRequest) {
	id := r.ID()
	metadata := M.Metadata{
		Source:      M.SocksaddrFrom(AddrFromAddress(id.RemoteAddress), id.RemotePort),
		Destination: M.SocksaddrFrom(AddrFromAddress(id.LocalAddress), id.LocalPort),
	}
	conn := newLazyTCPConn(f.ctx, metadata.Destination.TCPAddr(), metadata.Source.TCPAddr())
	go func() {
		defer conn.cancel(net.ErrClosed)
		defer conn.Close()
		if err := f.handler.NewConnection(conn.ctx, conn, metadata); err != nil {
			_ = conn.HandshakeFailure(err)
		}
	}()
	conn.runHandshake(f.done, func(ctx context.Context) (halfCloseTCPConn, error) {
		return acceptGVisorTCP(ctx, r)
	}, func() error {
		r.Complete(true)
		return nil
	}, func() error {
		r.Complete(false)
		return nil
	})
}

func acceptGVisorTCP(ctx context.Context, r *tcp.ForwarderRequest) (halfCloseTCPConn, error) {
	if err := ctx.Err(); err != nil {
		r.Complete(false)
		return nil, err
	}
	var wq waiter.Queue
	done := make(chan struct{})
	if ctx.Done() != nil {
		go func() {
			select {
			case <-ctx.Done():
				// Cancellation can race CreateEndpoint's waiter registration.
				// Keep waking it until the handshake has observed cancellation.
				ticker := time.NewTicker(time.Millisecond)
				defer ticker.Stop()
				for {
					wq.Notify(waiter.WritableEvents)
					select {
					case <-done:
						return
					case <-ticker.C:
					}
				}
			case <-done:
			}
		}()
	}
	endpoint, err := r.CreateEndpoint(&wq)
	close(done)
	if err != nil {
		r.Complete(true)
		return nil, gonet.TranslateNetstackError(err)
	}
	r.Complete(false)
	if err := ctx.Err(); err != nil {
		endpoint.Abort()
		return nil, err
	}
	endpoint.SocketOptions().SetKeepAlive(true)
	keepAliveIdle := tcpip.KeepaliveIdleOption(15 * time.Second)
	endpoint.SetSockOpt(&keepAliveIdle)
	keepAliveInterval := tcpip.KeepaliveIntervalOption(15 * time.Second)
	endpoint.SetSockOpt(&keepAliveInterval)
	return &gVisorTCPConn{TCPConn: gonet.NewTCPConn(&wq, endpoint), endpoint: endpoint}, nil
}

type gVisorTCPConn struct {
	*gonet.TCPConn
	endpoint tcpip.Endpoint
}

func (c *gVisorTCPConn) SetLinger(seconds int) error {
	c.endpoint.SocketOptions().SetLinger(tcpip.LingerOption{
		Enabled: seconds >= 0,
		Timeout: time.Duration(seconds) * time.Second,
	})
	return nil
}
