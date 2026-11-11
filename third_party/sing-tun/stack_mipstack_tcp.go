package tun

import (
	"context"
	"net"

	mips "github.com/metacubex/mipstack"
	M "github.com/metacubex/sing/common/metadata"
)

func (s *Mipstack) forwardTCP(request *mips.TCPForwarderRequest) {
	flow := request.Flow()
	conn := newLazyTCPConn(s.ctx, net.TCPAddrFromAddrPort(flow.Destination), net.TCPAddrFromAddrPort(flow.Source))
	metadata := M.Metadata{
		Source:      M.SocksaddrFromNetIP(flow.Source),
		Destination: M.SocksaddrFromNetIP(flow.Destination),
	}
	go func() {
		defer conn.cancel(net.ErrClosed)
		defer conn.Close()
		if err := s.handler.NewConnection(conn.ctx, conn, metadata); err != nil {
			_ = conn.HandshakeFailure(err)
		}
	}()
	conn.runHandshake(request.Done(), func(ctx context.Context) (halfCloseTCPConn, error) {
		accepted, err := request.Accept(ctx)
		if err != nil {
			return nil, err
		}
		return accepted, nil
	}, request.Reject, request.Drop)
}
