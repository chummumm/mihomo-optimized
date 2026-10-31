//go:build with_gvisor && !no_tailscale

package outbound

import (
	"context"
	"errors"
	"testing"
	"time"
)

func TestLANTailscaleWaiterCancellationDoesNotAbortSharedStartup(t *testing.T) {
	lifetime, closeInstance := context.WithCancel(context.Background())
	defer closeInstance()
	tail := &Tailscale{ctx: lifetime, cancel: closeInstance, startDone: make(chan struct{}), backendInitCh: make(chan struct{}), closeDone: make(chan struct{})}
	// Model an existing native Start operation without making a control-plane
	// login request. The waiter must remain caller-bounded even while it runs.
	tail.startOnce.Do(func() {})
	first, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if err := tail.ensureStarted(first); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("waiter error=%v", err)
	}
	if lifetime.Err() != nil {
		t.Fatal("request cancellation stopped shared native startup")
	}
	close(tail.startDone)
	tail.setBackendInitialized(nil)
	if err := tail.ensureStarted(context.Background()); err != nil {
		t.Fatal("later request cannot reuse startup:", err)
	}
	if err := tail.Close(); err != nil {
		t.Fatal(err)
	}
	if err := tail.Close(); err != nil {
		t.Fatal("repeated close:", err)
	}
}

func TestLANTailscaleCloseCancelsPendingReadyWait(t *testing.T) {
	lifetime, cancel := context.WithCancel(context.Background())
	tail := &Tailscale{ctx: lifetime, cancel: cancel, startDone: make(chan struct{}), backendInitCh: make(chan struct{}), closeDone: make(chan struct{})}
	tail.startOnce.Do(func() { close(tail.startDone) })
	result := make(chan error, 1)
	go func() { result <- tail.ensureStarted(context.Background()) }()
	if err := tail.Close(); err != nil {
		t.Fatal(err)
	}
	select {
	case err := <-result:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("close waiter error=%v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("close left backend waiter blocked")
	}
}
