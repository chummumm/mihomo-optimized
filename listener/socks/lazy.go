package socks

import (
	"net"
	"os"
	"sync"
	"sync/atomic"

	"github.com/metacubex/mihomo/transport/socks4"
	"github.com/metacubex/mihomo/transport/socks5"
)

const (
	replyPending uint32 = iota
	replySucceeded
	replyFailed
)

// lazyConn keeps CONNECT pending until dialing succeeds. Reading application
// data for sniffing may grant the request earlier, as in sing-box's LazyConn.
type lazyConn struct {
	net.Conn
	version byte
	once    sync.Once
	state   atomic.Uint32
	err     error
}

func newLazyConn(conn net.Conn, version byte) *lazyConn {
	return &lazyConn{Conn: conn, version: version}
}

func (c *lazyConn) ConnHandshakeSuccess(remote net.Conn) error {
	c.once.Do(func() {
		var bound net.Addr
		if remote != nil {
			bound = remote.LocalAddr()
		}
		switch c.version {
		case socks4.Version:
			c.err = socks4.WriteResponse(c.Conn, socks4.RequestGranted, bound)
		case socks5.Version:
			c.err = socks5.WriteResponse(c.Conn, 0, bound)
		default:
			panic("unknown SOCKS version")
		}
		if c.err != nil {
			c.state.Store(replyFailed)
		} else {
			c.state.Store(replySucceeded)
		}
	})
	if c.err != nil {
		return c.err
	}
	if c.state.Load() != replySucceeded {
		return os.ErrInvalid
	}
	return nil
}

func (c *lazyConn) HandshakeSuccess() error {
	return c.ConnHandshakeSuccess(c.Conn)
}

func (c *lazyConn) HandshakeFailure(err error) error {
	var reported bool
	c.once.Do(func() {
		reported = true
		switch c.version {
		case socks4.Version:
			c.err = socks4.WriteResponse(c.Conn, socks4.RequestRejected, nil)
		case socks5.Version:
			c.err = socks5.WriteResponse(c.Conn, socks5.ReplyCodeForError(err), nil)
		default:
			panic("unknown SOCKS version")
		}
		c.state.Store(replyFailed)
	})
	if !reported {
		return os.ErrInvalid
	}
	return c.err
}

func (c *lazyConn) Read(p []byte) (int, error) {
	if err := c.HandshakeSuccess(); err != nil {
		return 0, err
	}
	return c.Conn.Read(p)
}

func (c *lazyConn) Write(p []byte) (int, error) {
	if err := c.HandshakeSuccess(); err != nil {
		return 0, err
	}
	return c.Conn.Write(p)
}

func (c *lazyConn) NeedHandshake() bool {
	return c.state.Load() == replyPending
}

func (c *lazyConn) ReaderReplaceable() bool {
	return c.state.Load() == replySucceeded
}

func (c *lazyConn) WriterReplaceable() bool {
	return c.state.Load() == replySucceeded
}

func (c *lazyConn) Upstream() any {
	return c.Conn
}
