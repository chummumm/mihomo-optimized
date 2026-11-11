package net

import (
	"errors"
	"io"
	"net"

	"github.com/metacubex/sing/common"
	"github.com/metacubex/sing/common/network"
)

// ReportConnHandshakeSuccess lets an inbound acknowledge the connection only
// after its outbound is ready. SOCKS can also use the outbound's bound address.
func ReportConnHandshakeSuccess(reporter any, conn net.Conn) error {
	if handshake, ok := common.Cast[interface{ ConnHandshakeSuccess(net.Conn) error }](reporter); ok {
		return handshake.ConnHandshakeSuccess(conn)
	}
	return network.ReportHandshakeSuccess(reporter)
}

// CloseOnHandshakeFailure must run before the first Close. Protocol inbounds
// send their existing failure reply; accepted TCP sockets use an abortive close.
// It must not be called for errors after the connection enters the relay.
func CloseOnHandshakeFailure(reporter io.Closer, err error) error {
	if err == nil {
		return reporter.Close()
	}
	if handshake, ok := common.Cast[network.HandshakeFailure](reporter); ok {
		err = errors.Join(err, handshake.HandshakeFailure(err))
	} else if tcp, ok := common.Cast[interface{ SetLinger(int) error }](reporter); ok {
		err = errors.Join(err, tcp.SetLinger(0))
	}
	return errors.Join(err, reporter.Close())
}

// DropHandshake silently discards an unaccepted TCP request. Inbounds which
// already accepted the connection keep the ordinary REJECT-DROP lifetime.
func DropHandshake(reporter any) bool {
	if !NeedHandshake(reporter) {
		return false
	}
	if handshake, ok := common.Cast[interface{ HandshakeDrop() error }](reporter); ok {
		return handshake.HandshakeDrop() == nil
	}
	return false
}
