package executor

import (
	"context"
	"errors"
	"testing"
)

func TestDesktopRecoveryDoesNotWaitBehindConfig(t *testing.T) {
	mux.Lock()
	defer mux.Unlock()
	if _, err := InspectDesktopHealth(context.Background()); !errors.Is(err, ErrRecoveryBusy) {
		t.Fatalf("health query did not report busy: %v", err)
	}
	if _, err := RecoverDesktop(context.Background()); !errors.Is(err, ErrRecoveryBusy) {
		t.Fatalf("recovery did not report busy: %v", err)
	}
}

func TestDesktopRecoveryHonorsCanceledRequest(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := InspectDesktopHealth(ctx); !errors.Is(err, context.Canceled) {
		t.Fatalf("canceled inspection proceeded: %v", err)
	}
	if _, err := RecoverDesktop(ctx); !errors.Is(err, context.Canceled) {
		t.Fatalf("canceled recovery proceeded: %v", err)
	}
}

func TestDesktopRecoveryHonorsStopIntent(t *testing.T) {
	previous := stopping.Swap(true)
	defer stopping.Store(previous)
	if _, err := InspectDesktopHealth(context.Background()); !errors.Is(err, ErrShuttingDown) {
		t.Fatalf("inspection bypassed stop admission: %v", err)
	}
	if _, err := RecoverDesktop(context.Background()); !errors.Is(err, ErrShuttingDown) {
		t.Fatalf("recovery bypassed stop admission: %v", err)
	}
}
