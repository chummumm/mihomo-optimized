package context

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	C "github.com/metacubex/mihomo/constant"
)

const EasyTierPrepareTimeout = 15 * time.Second

type tcpDialBudgetKey struct{}

type tcpDialPhase uint8

const (
	tcpDialOrdinary tcpDialPhase = iota
	tcpDialPreparing
	tcpDialConnecting
)

// A TCP request owns one budget across proxy groups, dialer-proxy chains and
// retries. Deadline reports the fixed outer limit, while a cause-carrying
// cancellation timer enforces the current phase: normally start+5s. Only an
// actually dialed EasyTier may claim start+15s for preparation, followed by
// one 5s connection phase, capped at start+20s. No parent deadline is removed.
// Keeping the phase timer here avoids installing an irrevocable 5s child
// deadline before a nested dialer-proxy selects EasyTier.
type tcpDialBudget struct {
	mu         sync.Mutex
	ctx        context.Context
	cancel     context.CancelCauseFunc
	start      time.Time
	prepare    time.Duration
	connect    time.Duration
	limit      time.Time
	deadline   time.Time
	phase      tcpDialPhase
	timer      *time.Timer
	generation uint64
	closed     bool
	expired    bool
}

// WithTCPDialBudget reuses a request budget when a nested adapter calls it.
// The returned cancel owns only a newly created budget, never its caller's.
func WithTCPDialBudget(parent context.Context) (context.Context, context.CancelFunc) {
	if _, ok := parent.Value(tcpDialBudgetKey{}).(*tcpDialBudget); ok {
		return parent, func() {}
	}
	return newTCPDialBudget(parent, EasyTierPrepareTimeout, C.DefaultTCPTimeout)
}

func newTCPDialBudget(parent context.Context, prepare, connect time.Duration) (context.Context, context.CancelFunc) {
	start := time.Now()
	limit := start.Add(prepare + connect)
	outer, cancelOuter := context.WithDeadline(parent, limit)
	ctx, cancel := context.WithCancelCause(outer)
	b := &tcpDialBudget{ctx: ctx, cancel: cancel, start: start, prepare: prepare, connect: connect, limit: limit}
	b.mu.Lock()
	b.setDeadlineLocked(start.Add(connect))
	b.mu.Unlock()
	return context.WithValue(ctx, tcpDialBudgetKey{}, b), func() {
		b.mu.Lock()
		b.closed = true
		b.generation++
		b.timer.Stop()
		b.mu.Unlock()
		cancel(context.Canceled)
		cancelOuter()
	}
}

func (b *tcpDialBudget) setDeadlineLocked(deadline time.Time) {
	if deadline.After(b.limit) {
		deadline = b.limit
	}
	b.deadline = deadline
	b.generation++
	if b.timer != nil {
		b.timer.Stop()
	}
	generation := b.generation
	b.timer = time.AfterFunc(time.Until(deadline), func() { b.expire(generation) })
}

func (b *tcpDialBudget) expire(generation uint64) {
	b.mu.Lock()
	if b.closed || generation != b.generation {
		b.mu.Unlock()
		return
	}
	b.closed, b.expired = true, true
	b.mu.Unlock()
	b.cancel(context.DeadlineExceeded)
}

func (b *tcpDialBudget) advance(phase tcpDialPhase) error {
	b.mu.Lock()
	if err := b.ctx.Err(); err != nil {
		b.mu.Unlock()
		return context.Cause(b.ctx)
	}
	// Timer delivery may be delayed. Never revive an expired phase merely
	// because its callback has not acquired the mutex yet.
	if b.closed || !time.Now().Before(b.deadline) {
		expired := b.expired || !b.closed
		b.closed, b.expired = true, expired
		b.generation++
		b.timer.Stop()
		b.mu.Unlock()
		if expired {
			b.cancel(context.DeadlineExceeded)
			return context.DeadlineExceeded
		}
		return context.Canceled
	}
	if phase > b.phase {
		b.phase = phase
		if phase == tcpDialPreparing {
			b.setDeadlineLocked(b.start.Add(b.prepare))
		} else {
			b.setDeadlineLocked(time.Now().Add(b.connect))
		}
	}
	b.mu.Unlock()
	return nil
}

// BeginTCPPreparation is called by the selected EasyTier, not by a speculative
// group unwrap. Repeated calls after connection has begun cannot renew time.
func BeginTCPPreparation(ctx context.Context) error {
	if b, ok := ctx.Value(tcpDialBudgetKey{}).(*tcpDialBudget); ok {
		if err := ctx.Err(); err != nil {
			return TCPDialError(ctx, err)
		}
		return b.advance(tcpDialPreparing)
	}
	return ctx.Err()
}

func BeginTCPConnection(ctx context.Context) error {
	if b, ok := ctx.Value(tcpDialBudgetKey{}).(*tcpDialBudget); ok {
		if err := ctx.Err(); err != nil {
			return TCPDialError(ctx, err)
		}
		return b.advance(tcpDialConnecting)
	}
	return ctx.Err()
}

// TCPDialError restores the timeout classification only for cancellation
// caused by this request's own budget. Server errors and caller cancellation
// retain their original identity, including errors racing a local timeout.
func TCPDialError(ctx context.Context, err error) error {
	if err == nil || !errors.Is(err, context.Canceled) {
		return err
	}
	b, ok := ctx.Value(tcpDialBudgetKey{}).(*tcpDialBudget)
	if !ok || context.Cause(b.ctx) != context.DeadlineExceeded {
		return err
	}
	b.mu.Lock()
	expired := b.expired
	b.mu.Unlock()
	if !expired {
		return err
	}
	if err == context.Canceled {
		return context.DeadlineExceeded
	}
	return fmt.Errorf("%w: %v", context.DeadlineExceeded, err)
}
