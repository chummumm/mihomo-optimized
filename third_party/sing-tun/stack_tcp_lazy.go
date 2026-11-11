package tun

import (
	"context"
	"net"
	"os"
	"sync"
	"time"
)

type tcpHandshakeAction uint8

const (
	tcpHandshakeAccept tcpHandshakeAction = iota
	tcpHandshakeReject
	tcpHandshakeDrop
)

type tcpHandshakeState uint8

const (
	tcpHandshakePending tcpHandshakeState = iota
	tcpHandshakeRunning
	tcpHandshakeComplete
)

type tcpHandshakeDeadline uint8

const (
	tcpHandshakeNoDeadline tcpHandshakeDeadline = iota
	tcpHandshakeReadDeadline
	tcpHandshakeWriteDeadline
)

type halfCloseTCPConn interface {
	net.Conn
	CloseRead() error
	CloseWrite() error
	SetLinger(int) error
}

// lazyTCPConn leaves an intercepted SYN unanswered until the outbound is ready.
// The forwarder owns the request and executes its one terminal action; callers
// only send decisions, so a mipstack request never escapes its callback.
type lazyTCPConn struct {
	ctx               context.Context
	cancel            context.CancelCauseFunc
	localAddr         net.Addr
	remoteAddr        net.Addr
	mu                sync.Mutex
	state             tcpHandshakeState
	action            tcpHandshakeAction
	actions           chan tcpHandshakeAction
	ready             chan struct{}
	conn              halfCloseTCPConn
	err               error
	actionErr         error
	readDeadline      time.Time
	writeDeadline     time.Time
	handshakeDeadline tcpHandshakeDeadline
	deadlineTimer     *time.Timer
}

func newLazyTCPConn(ctx context.Context, localAddr, remoteAddr net.Addr) *lazyTCPConn {
	ctx, cancel := context.WithCancelCause(ctx)
	return &lazyTCPConn{
		ctx:        ctx,
		cancel:     cancel,
		localAddr:  localAddr,
		remoteAddr: remoteAddr,
		actions:    make(chan tcpHandshakeAction, 1),
		ready:      make(chan struct{}),
	}
}

// runHandshake must run in the original forwarder callback. requestDone belongs
// only to the pending request: after acceptance this method returns and never
// associates request cancellation with the established connection.
func (c *lazyTCPConn) runHandshake(requestDone <-chan struct{}, accept func(context.Context) (halfCloseTCPConn, error), reject, drop func() error) {
	select {
	case action := <-c.actions:
		switch action {
		case tcpHandshakeAccept:
			conn, err := accept(c.ctx)
			if err != nil {
				if cause := context.Cause(c.ctx); cause != nil {
					err = cause
				}
			}
			c.finishHandshake(conn, err, err)
		case tcpHandshakeReject:
			c.finishHandshake(nil, c.handshakeError(), reject())
		case tcpHandshakeDrop:
			c.finishHandshake(nil, ErrDrop, drop())
		}
	case <-requestDone:
		c.cancel(net.ErrClosed)
		_ = drop() // An invalidated request may already have been dropped.
		c.finishHandshake(nil, net.ErrClosed, nil)
	case <-c.ctx.Done():
		_ = drop()
		c.finishHandshake(nil, context.Cause(c.ctx), nil)
	}
}

func (c *lazyTCPConn) finishHandshake(conn halfCloseTCPConn, err, actionErr error) {
	c.mu.Lock()
	if c.deadlineTimer != nil {
		c.deadlineTimer.Stop()
	}
	if conn != nil && err == nil {
		if err = conn.SetReadDeadline(c.readDeadline); err == nil {
			err = conn.SetWriteDeadline(c.writeDeadline)
		}
		if err != nil {
			_ = conn.SetLinger(0)
			_ = conn.Close()
			conn = nil
			actionErr = err
		}
	}
	c.conn, c.err, c.actionErr = conn, err, actionErr
	c.state = tcpHandshakeComplete
	close(c.ready)
	c.mu.Unlock()
	if err != nil {
		c.cancel(err)
	}
}

func (c *lazyTCPConn) beginHandshakeLocked(action tcpHandshakeAction, deadline tcpHandshakeDeadline) {
	c.state = tcpHandshakeRunning
	c.action = action
	c.handshakeDeadline = deadline
	if action == tcpHandshakeAccept {
		c.updateHandshakeDeadlineLocked()
	}
	c.actions <- action
}

func (c *lazyTCPConn) handshake(deadline tcpHandshakeDeadline) error {
	c.mu.Lock()
	if c.state == tcpHandshakePending {
		c.beginHandshakeLocked(tcpHandshakeAccept, deadline)
	}
	c.mu.Unlock()
	<-c.ready
	return c.handshakeError()
}

func (c *lazyTCPConn) handshakeError() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.err
}

func (c *lazyTCPConn) NeedHandshake() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.state != tcpHandshakeComplete
}

func (c *lazyTCPConn) HandshakeSuccess() error {
	return c.handshake(tcpHandshakeWriteDeadline)
}

func (c *lazyTCPConn) HandshakeFailure(err error) error {
	if err == nil {
		err = net.ErrClosed
	}
	c.mu.Lock()
	if c.state == tcpHandshakePending {
		c.err = err
		c.beginHandshakeLocked(tcpHandshakeReject, tcpHandshakeNoDeadline)
	} else if c.state == tcpHandshakeRunning && c.action == tcpHandshakeAccept {
		c.cancel(err)
	}
	c.mu.Unlock()
	<-c.ready
	c.mu.Lock()
	conn, actionErr := c.conn, c.actionErr
	c.mu.Unlock()
	if conn != nil {
		// Sniffing may already have accepted the local handshake. Arrange an
		// abortive close before the caller closes that established endpoint.
		return conn.SetLinger(0)
	}
	return actionErr
}

func (c *lazyTCPConn) HandshakeDrop() error {
	c.mu.Lock()
	if c.state != tcpHandshakePending {
		c.mu.Unlock()
		return os.ErrInvalid
	}
	c.beginHandshakeLocked(tcpHandshakeDrop, tcpHandshakeNoDeadline)
	c.mu.Unlock()
	<-c.ready
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.actionErr
}

func (c *lazyTCPConn) Read(p []byte) (int, error) {
	if err := c.handshake(tcpHandshakeReadDeadline); err != nil {
		return 0, err
	}
	return c.conn.Read(p)
}

func (c *lazyTCPConn) Write(p []byte) (int, error) {
	if err := c.handshake(tcpHandshakeWriteDeadline); err != nil {
		return 0, err
	}
	return c.conn.Write(p)
}

func (c *lazyTCPConn) LocalAddr() net.Addr  { return c.localAddr }
func (c *lazyTCPConn) RemoteAddr() net.Addr { return c.remoteAddr }

func (c *lazyTCPConn) SetDeadline(t time.Time) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.readDeadline, c.writeDeadline = t, t
	if c.conn != nil {
		return c.conn.SetDeadline(t)
	}
	c.updateHandshakeDeadlineLocked()
	return c.err
}

func (c *lazyTCPConn) SetReadDeadline(t time.Time) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.readDeadline = t
	if c.conn != nil {
		return c.conn.SetReadDeadline(t)
	}
	c.updateHandshakeDeadlineLocked()
	return c.err
}

func (c *lazyTCPConn) SetWriteDeadline(t time.Time) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.writeDeadline = t
	if c.conn != nil {
		return c.conn.SetWriteDeadline(t)
	}
	c.updateHandshakeDeadlineLocked()
	return c.err
}

// Deadlines do not accept a SYN. Once Read or Write starts the handshake, its
// deadline also bounds that wait and can still be changed by another goroutine.
func (c *lazyTCPConn) updateHandshakeDeadlineLocked() {
	if c.deadlineTimer != nil {
		c.deadlineTimer.Stop()
	}
	if c.state != tcpHandshakeRunning || c.action != tcpHandshakeAccept {
		return
	}
	deadline := c.activeHandshakeDeadlineLocked()
	if deadline.IsZero() {
		return
	}
	c.deadlineTimer = time.AfterFunc(time.Until(deadline), func() {
		c.mu.Lock()
		defer c.mu.Unlock()
		if c.state == tcpHandshakeRunning && c.activeHandshakeDeadlineLocked().Equal(deadline) {
			c.cancel(os.ErrDeadlineExceeded)
		}
	})
}

func (c *lazyTCPConn) activeHandshakeDeadlineLocked() time.Time {
	switch c.handshakeDeadline {
	case tcpHandshakeReadDeadline:
		return c.readDeadline
	case tcpHandshakeWriteDeadline:
		return c.writeDeadline
	default:
		return time.Time{}
	}
}

func (c *lazyTCPConn) SetLinger(seconds int) error {
	c.mu.Lock()
	conn := c.conn
	c.mu.Unlock()
	if conn == nil {
		return os.ErrInvalid
	}
	return conn.SetLinger(seconds)
}

func (c *lazyTCPConn) Close() error {
	conn := c.closeBeforeHandshake()
	if conn == nil {
		return nil
	}
	return conn.Close()
}

func (c *lazyTCPConn) CloseRead() error {
	conn := c.closeBeforeHandshake()
	if conn == nil {
		return nil
	}
	return conn.CloseRead()
}

func (c *lazyTCPConn) CloseWrite() error {
	conn := c.closeBeforeHandshake()
	if conn == nil {
		return nil
	}
	return conn.CloseWrite()
}

func (c *lazyTCPConn) closeBeforeHandshake() halfCloseTCPConn {
	c.mu.Lock()
	if c.state == tcpHandshakePending {
		c.err = net.ErrClosed
		c.beginHandshakeLocked(tcpHandshakeReject, tcpHandshakeNoDeadline)
	} else if c.state == tcpHandshakeRunning && c.action == tcpHandshakeAccept {
		c.cancel(net.ErrClosed)
	}
	c.mu.Unlock()
	<-c.ready
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.conn
}

func (c *lazyTCPConn) ReaderReplaceable() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.conn != nil
}

func (c *lazyTCPConn) WriterReplaceable() bool { return c.ReaderReplaceable() }

func (c *lazyTCPConn) Upstream() any {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.conn
}
