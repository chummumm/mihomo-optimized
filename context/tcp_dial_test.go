package context

import (
	"context"
	"errors"
	"testing"
	"time"
)

func dialBudgetForTest(ctx context.Context) *tcpDialBudget {
	return ctx.Value(tcpDialBudgetKey{}).(*tcpDialBudget)
}

func TestTCPDialBudgetOrdinaryFiveSecondsAndAbsoluteLimit(t *testing.T) {
	ctx, cancel := WithTCPDialBudget(context.Background())
	defer cancel()
	b := dialBudgetForTest(ctx)
	if got := b.deadline.Sub(b.start); got != 5*time.Second {
		t.Fatalf("ordinary budget = %s", got)
	}
	deadline, _ := ctx.Deadline()
	if got := deadline.Sub(b.start); got != 20*time.Second {
		t.Fatalf("outer limit = %s", got)
	}
	if err := BeginTCPPreparation(ctx); err != nil {
		t.Fatal(err)
	}
	if got := b.deadline.Sub(b.start); got != 15*time.Second {
		t.Fatalf("preparation renewed from call time: %s", got)
	}
}

func TestTCPDialBudgetCancelsOrdinaryWithoutLeafProgress(t *testing.T) {
	ctx, cancel := newTCPDialBudget(context.Background(), time.Second, 20*time.Millisecond)
	defer cancel()
	select {
	case <-ctx.Done():
		if !errors.Is(TCPDialError(ctx, ctx.Err()), context.DeadlineExceeded) {
			t.Fatalf("wrong deadline classification: %v / %v", ctx.Err(), context.Cause(ctx))
		}
	case <-time.After(time.Second):
		t.Fatal("ordinary request survived its original budget")
	}
}

func TestTCPDialBudgetOldTimerCannotCancelPreparation(t *testing.T) {
	ctx, cancel := WithTCPDialBudget(context.Background())
	defer cancel()
	b := dialBudgetForTest(ctx)
	oldGeneration := b.generation
	if err := BeginTCPPreparation(ctx); err != nil {
		t.Fatal(err)
	}
	// Model an old callback already queued when Timer.Stop returned false.
	b.expire(oldGeneration)
	if ctx.Err() != nil {
		t.Fatal("old ordinary timer canceled the preparation phase")
	}
	currentGeneration := b.generation
	b.expire(currentGeneration)
	if err := BeginTCPConnection(ctx); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("expired preparation revived: %v", err)
	}
}

func TestTCPDialBudgetDelayedTimerCannotReviveElapsedDeadline(t *testing.T) {
	for _, next := range []tcpDialPhase{tcpDialPreparing, tcpDialConnecting} {
		ctx, cancel := WithTCPDialBudget(context.Background())
		b := dialBudgetForTest(ctx)
		b.mu.Lock()
		b.deadline = time.Now().Add(-time.Second)
		b.mu.Unlock()
		if err := b.advance(next); !errors.Is(err, context.DeadlineExceeded) {
			t.Errorf("phase %d revived an elapsed deadline: %v", next, err)
		}
		cancel()
	}
}

func TestTCPDialBudgetConnectionDeadlineDoesNotRenewAcrossRetries(t *testing.T) {
	ctx, cancel := WithTCPDialBudget(context.Background())
	defer cancel()
	b := dialBudgetForTest(ctx)
	if err := BeginTCPPreparation(ctx); err != nil {
		t.Fatal(err)
	}
	if err := BeginTCPConnection(ctx); err != nil {
		t.Fatal(err)
	}
	deadline := b.deadline
	for i := 0; i < 20; i++ {
		if err := BeginTCPPreparation(ctx); err != nil {
			t.Fatal(err)
		}
		if err := BeginTCPConnection(ctx); err != nil {
			t.Fatal(err)
		}
		if !b.deadline.Equal(deadline) {
			t.Fatal("a retry renewed the TCP connection budget")
		}
	}
}

func TestTCPDialBudgetConnectionIsCappedByOriginalTotal(t *testing.T) {
	ctx, cancel := WithTCPDialBudget(context.Background())
	defer cancel()
	b := dialBudgetForTest(ctx)
	if err := BeginTCPPreparation(ctx); err != nil {
		t.Fatal(err)
	}
	// Deterministically model less than five seconds remaining in the total.
	b.mu.Lock()
	b.limit = time.Now().Add(time.Second)
	limit := b.limit
	b.mu.Unlock()
	if err := BeginTCPConnection(ctx); err != nil {
		t.Fatal(err)
	}
	if !b.deadline.Equal(limit) {
		t.Fatalf("connection exceeded total: %v > %v", b.deadline, limit)
	}
}

func TestTCPDialBudgetNestedAdaptersSharePreparationAndCancellation(t *testing.T) {
	parent, stopParent := context.WithCancel(context.Background())
	ctx, cancel := WithTCPDialBudget(parent)
	defer cancel()
	child, cancelChild := WithTCPDialBudget(context.WithValue(ctx, "nested", true))
	cancelChild()
	if dialBudgetForTest(child) != dialBudgetForTest(ctx) || ctx.Err() != nil {
		t.Fatal("nested adapter owned or replaced the caller's budget")
	}
	if err := BeginTCPPreparation(child); err != nil {
		t.Fatal(err)
	}
	stopParent()
	<-child.Done()
	if err := TCPDialError(child, child.Err()); err != context.Canceled {
		t.Fatalf("caller cancellation mislabeled: %v", err)
	}
}

func TestTCPDialBudgetRespectsEarlierParentDeadline(t *testing.T) {
	parent := &tcpDialTestDeadlineContext{
		Context: context.Background(), deadline: time.Now().Add(time.Second), done: make(chan struct{}),
	}
	ctx, cancel := WithTCPDialBudget(parent)
	defer cancel()
	if err := BeginTCPPreparation(ctx); err != nil {
		t.Fatal(err)
	}
	parentDeadline, _ := parent.Deadline()
	deadline, _ := ctx.Deadline()
	if !deadline.Equal(parentDeadline) {
		t.Fatal("request replaced the caller's deadline")
	}
	// Deliver the parent's deadline after preparation starts, without relying
	// on a short wall-clock timer racing the test runner under -race.
	close(parent.done)
	<-ctx.Done()
	if !errors.Is(BeginTCPConnection(ctx), context.DeadlineExceeded) {
		t.Fatal("parent timeout did not stop connection")
	}
}

type tcpDialTestDeadlineContext struct {
	context.Context
	deadline time.Time
	done     chan struct{}
}

func (c *tcpDialTestDeadlineContext) Deadline() (time.Time, bool) { return c.deadline, true }
func (c *tcpDialTestDeadlineContext) Done() <-chan struct{}       { return c.done }
func (c *tcpDialTestDeadlineContext) Err() error {
	select {
	case <-c.done:
		return context.DeadlineExceeded
	default:
		return nil
	}
}

func TestTCPDialBudgetPreservesRemoteErrorsRacingTimeout(t *testing.T) {
	ctx, cancel := WithTCPDialBudget(context.Background())
	defer cancel()
	b := dialBudgetForTest(ctx)
	b.expire(b.generation)
	serverErr := errors.New("server refused connection")
	if TCPDialError(ctx, serverErr) != serverErr || TCPDialError(ctx, nil) != nil {
		t.Fatal("local timer relabeled a remote result")
	}
}
