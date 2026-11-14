package net

import (
	"io"
	"net"

	"github.com/metacubex/sing/common/buf"
	"github.com/metacubex/sing/common/bufio"
	"github.com/metacubex/sing/common/uot"
)

// NewUOTConn preserves payload write counts with sing v0.5.9's legacy UOT
// fallback, which otherwise returns the size of the payload plus its header.
func NewUOTConn(conn net.Conn, request uot.Request) *uot.Conn {
	if _, ok := bufio.CreateVectorisedWriter(conn); !ok {
		conn = &uotVectorisedConn{Conn: conn}
	}
	return uot.NewConn(conn, request)
}

type uotVectorisedConn struct {
	net.Conn
}

func (c *uotVectorisedConn) WriteVectorised(buffers []*buf.Buffer) error {
	defer buf.ReleaseMulti(buffers)
	frameLength := buf.LenMulti(buffers)
	frame := buf.NewSize(frameLength)
	defer frame.Release()
	buf.CopyMulti(frame.Extend(frameLength), buffers)

	// Keep the fallback's single frame write. Only a complete frame allows
	// uot.Conn to report success using the original payload length.
	n, err := c.Conn.Write(frame.Bytes())
	if err != nil {
		return err
	}
	if n != frameLength {
		return io.ErrShortWrite
	}
	return nil
}

func (c *uotVectorisedConn) Upstream() any {
	return c.Conn
}
