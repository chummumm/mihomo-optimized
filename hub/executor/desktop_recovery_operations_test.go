package executor

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/mihomo/listener"
)

func isolateDesktopRecovery(t *testing.T) (*desktopRecoveryCoordinator, func(listener.TunObservation)) {
	t.Helper()
	previous, wasStopping := desktopRecoveries, stopping.Swap(false)
	manager := newDesktopRecoveryCoordinator(time.Second)
	manager.ready, manager.supported = func() bool { return true }, true
	var observation atomic.Pointer[listener.TunObservation]
	observation.Store(&listener.TunObservation{Tun: listener.TunHealth{State: "disabled"}})
	manager.observe = func() listener.TunObservation { return *observation.Load() }
	manager.recover = func(context.Context) (DesktopRecovery, error) {
		return DesktopRecovery{Status: "healthy", Tun: manager.observe().Tun}, nil
	}
	desktopRecoveries = manager
	t.Cleanup(func() {
		manager.cancel()
		manager.mu.Lock()
		active := manager.active
		manager.mu.Unlock()
		if active != nil {
			select {
			case <-active.done:
			case <-time.After(2 * time.Second):
				t.Error("recovery worker survived test cleanup")
			}
		}
		desktopRecoveries = previous
		stopping.Store(wasStopping)
	})
	return manager, func(value listener.TunObservation) { observation.Store(&value) }
}

func waitDesktopRecovery(t *testing.T, manager *desktopRecoveryCoordinator, id string) DesktopRecoveryOperation {
	t.Helper()
	manager.mu.Lock()
	work := manager.records[id]
	manager.mu.Unlock()
	if work == nil {
		t.Fatalf("unknown operation %s", id)
	}
	select {
	case <-work.done:
	case <-time.After(2 * time.Second):
		t.Fatal("recovery operation did not finish")
	}
	result, err := manager.get(id)
	if err != nil {
		t.Fatal(err)
	}
	return result
}

func TestDesktopRecoveryAcceptedWorkSurvivesLostHTTPResponse(t *testing.T) {
	manager, _ := isolateDesktopRecovery(t)
	entered, release := make(chan struct{}), make(chan struct{})
	var releaseOnce sync.Once
	t.Cleanup(func() { releaseOnce.Do(func() { close(release) }) })
	var calls atomic.Int32
	manager.recover = func(ctx context.Context) (DesktopRecovery, error) {
		calls.Add(1)
		close(entered)
		<-release
		if err := ctx.Err(); err != nil {
			return DesktopRecovery{Status: "unavailable"}, err
		}
		return DesktopRecovery{Status: "healthy", Tun: manager.observe().Tun}, nil
	}
	request := DesktopRecoveryRequest{RequestID: "lost-response"}
	requestCtx, cancelRequest := context.WithCancel(context.Background())
	accepted, err := StartDesktopRecovery(requestCtx, request)
	if err != nil || accepted.Phase != "running" {
		t.Fatalf("operation was not accepted: %+v %v", accepted, err)
	}
	<-entered
	cancelRequest()
	replayed, err := StartDesktopRecovery(context.Background(), request)
	if err != nil || replayed.ID != accepted.ID || calls.Load() != 1 {
		t.Fatalf("lost response started duplicate work: original=%+v replay=%+v err=%v calls=%d", accepted, replayed, err, calls.Load())
	}
	if _, err := StartDesktopRecovery(context.Background(), DesktopRecoveryRequest{RequestID: "overlap"}); !errors.Is(err, ErrRecoveryBusy) {
		t.Fatalf("second native operation was admitted: %v", err)
	}
	if _, err := InspectDesktopObservation(context.Background()); err != nil {
		t.Fatalf("health read blocked behind accepted work: %v", err)
	}
	if mux.TryLock() {
		mux.Unlock()
		t.Fatal("native operation did not retain executor ownership")
	}
	releaseOnce.Do(func() { close(release) })
	finished := waitDesktopRecovery(t, manager, accepted.ID)
	if finished.Phase != "succeeded" || finished.Status != "healthy" {
		t.Fatalf("HTTP cancellation cancelled accepted server work: %+v", finished)
	}
	replayed, err = StartDesktopRecovery(context.Background(), request)
	if err != nil || replayed != finished || calls.Load() != 1 {
		t.Fatalf("terminal result was not idempotent: %+v err=%v calls=%d", replayed, err, calls.Load())
	}
}

func TestDesktopRecoveryDeadlineRetainsExclusiveNativeOwnership(t *testing.T) {
	manager, setObservation := isolateDesktopRecovery(t)
	setObservation(listener.TunObservation{Tun: listener.TunHealth{Enabled: true, State: "unavailable"}, Stale: true})
	manager.limit = 25 * time.Millisecond
	release := make(chan struct{})
	var releaseOnce sync.Once
	t.Cleanup(func() { releaseOnce.Do(func() { close(release) }) })
	manager.recover = func(context.Context) (DesktopRecovery, error) {
		// Simulate a native call which cannot observe cancellation until it
		// returns. A per-request timeout must not allow a second constructor.
		<-release
		return DesktopRecovery{Status: "healthy"}, nil
	}
	accepted, err := StartDesktopRecovery(context.Background(), DesktopRecoveryRequest{RequestID: "stuck-native-call"})
	if err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(2 * time.Second)
	var expired DesktopRecoveryOperation
	for time.Now().Before(deadline) {
		expired, err = GetDesktopRecovery(accepted.ID)
		if err == nil && expired.Phase == "failed" {
			break
		}
		time.Sleep(time.Millisecond)
	}
	if expired.Phase != "failed" || expired.Status != "needs_restart" || !strings.Contains(expired.Error, "native operation remains owned") {
		t.Fatalf("stuck operation did not publish bounded failure: %+v err=%v", expired, err)
	}
	health, err := InspectDesktopObservation(context.Background())
	if err != nil || health.Tun.State != "degraded" || !health.Tun.Repairable || !health.Stale || health.Recovery == nil || health.Recovery.ID != accepted.ID {
		t.Fatalf("stuck native failure became permanently unobservable to a v1 supervisor: %+v %v", health, err)
	}
	if mux.TryLock() {
		mux.Unlock()
		t.Fatal("deadline abandoned the native lifecycle lock")
	}
	if _, err := StartDesktopRecovery(context.Background(), DesktopRecoveryRequest{RequestID: "must-not-overlap"}); !errors.Is(err, ErrRecoveryBusy) {
		t.Fatalf("deadline admitted overlapping work: %v", err)
	}
	if legacy, err := RecoverDesktop(context.Background()); err != nil || legacy.Status != "needs_restart" {
		t.Fatalf("old supervisor could not see stuck operation: %+v %v", legacy, err)
	}
	releaseOnce.Do(func() { close(release) })
	finished := waitDesktopRecovery(t, manager, accepted.ID)
	if finished != expired {
		t.Fatalf("late native completion overwrote terminal failure: expired=%+v finished=%+v", expired, finished)
	}
}

func TestDesktopShutdownCancelsOwnedRecovery(t *testing.T) {
	manager, _ := isolateDesktopRecovery(t)
	entered := make(chan struct{})
	manager.recover = func(ctx context.Context) (DesktopRecovery, error) {
		close(entered)
		<-ctx.Done()
		return DesktopRecovery{Status: "unavailable"}, ctx.Err()
	}
	accepted, err := StartDesktopRecovery(context.Background(), DesktopRecoveryRequest{RequestID: "cancel-on-stop"})
	if err != nil {
		t.Fatal(err)
	}
	<-entered
	RequestShutdown()
	finished := waitDesktopRecovery(t, manager, accepted.ID)
	if finished.Phase != "cancelled" || !ShutdownRequested() {
		t.Fatalf("stop did not cancel operation owner: %+v", finished)
	}
	if _, err := StartDesktopRecovery(context.Background(), DesktopRecoveryRequest{RequestID: "after-stop"}); !errors.Is(err, ErrShuttingDown) {
		t.Fatalf("stop admitted fresh work: %v", err)
	}
}

func TestDesktopRecoveryRejectsStaleGenerationAndChangedIdempotencyKey(t *testing.T) {
	manager, setObservation := isolateDesktopRecovery(t)
	setObservation(listener.TunObservation{ConfigGeneration: 3, TunGeneration: 4, Tun: listener.TunHealth{State: "healthy"}})
	if _, err := StartDesktopRecovery(context.Background(), DesktopRecoveryRequest{RequestID: "stale", ConfigGeneration: 3, TunGeneration: 2}); !errors.Is(err, ErrRecoverySuperseded) {
		t.Fatalf("stale device generation was accepted: %v", err)
	}
	request := DesktopRecoveryRequest{RequestID: "same-key", ConfigGeneration: 3, TunGeneration: 4}
	accepted, err := StartDesktopRecovery(context.Background(), request)
	if err != nil {
		t.Fatal(err)
	}
	finished := waitDesktopRecovery(t, manager, accepted.ID)
	setObservation(listener.TunObservation{ConfigGeneration: 4, TunGeneration: 5, Tun: listener.TunHealth{State: "healthy"}})
	if replay, err := StartDesktopRecovery(context.Background(), request); err != nil || replay != finished {
		t.Fatalf("generation change erased already accepted result: %+v %v", replay, err)
	}
	request.ConfigGeneration, request.TunGeneration = 4, 5
	if _, err := StartDesktopRecovery(context.Background(), request); !errors.Is(err, ErrRecoverySuperseded) {
		t.Fatalf("same key silently retargeted new device: %v", err)
	}
	if current := manager.current(manager.observe()); current != nil {
		t.Fatalf("old operation attached to a new device lifetime: %+v", current)
	}
}

func TestDesktopLegacyRecoveryRetainsFailureAfterDroppedReply(t *testing.T) {
	manager, setObservation := isolateDesktopRecovery(t)
	failure := listener.TunObservation{ConfigGeneration: 1, TunGeneration: 2,
		Tun: listener.TunHealth{Enabled: true, State: "degraded", Repairable: true, Reason: "owned TUN replacement failed"}}
	setObservation(failure)
	var calls atomic.Int32
	manager.recover = func(context.Context) (DesktopRecovery, error) {
		calls.Add(1)
		return DesktopRecovery{Status: "needs_restart", Tun: failure.Tun, Error: "driver creation failed"}, nil
	}
	accepted, err := StartDesktopRecovery(context.Background(), DesktopRecoveryRequest{RequestID: "dropped-failure", ConfigGeneration: 1, TunGeneration: 2})
	if err != nil {
		t.Fatal(err)
	}
	waitDesktopRecovery(t, manager, accepted.ID)
	for i := 0; i < 3; i++ {
		result, err := RecoverDesktop(context.Background())
		if err != nil || result.Status != "needs_restart" || result.Error != "driver creation failed" || calls.Load() != 1 {
			t.Fatalf("legacy retry lost terminal failure or reran constructor: %+v err=%v calls=%d", result, err, calls.Load())
		}
	}
}

func TestDesktopLegacyHealthyResultDoesNotMaskLaterRouteLoss(t *testing.T) {
	manager, setObservation := isolateDesktopRecovery(t)
	healthy := listener.TunObservation{ConfigGeneration: 1, TunGeneration: 2,
		Tun: listener.TunHealth{Enabled: true, State: "healthy", Device: "owned", Index: 42}}
	degraded := healthy
	degraded.Tun.State, degraded.Tun.Reason, degraded.Tun.Repairable = "degraded", "owned TUN route is missing", true
	setObservation(healthy)
	if result, err := RecoverDesktop(context.Background()); err != nil || result.Status != "healthy" {
		t.Fatalf("healthy legacy recovery changed resources: %+v %v", result, err)
	}
	var calls atomic.Int32
	manager.recover = func(context.Context) (DesktopRecovery, error) {
		calls.Add(1)
		setObservation(healthy)
		return DesktopRecovery{Status: "repaired", Tun: healthy.Tun}, nil
	}
	for i := 1; i <= 2; i++ {
		setObservation(degraded)
		if current := manager.current(degraded); current != nil {
			t.Fatalf("previous success hid new route loss: %+v", current)
		}
		if _, err := RecoverDesktop(context.Background()); !errors.Is(err, ErrRecoveryBusy) {
			t.Fatalf("new route repair was not asynchronously admitted: %v", err)
		}
		manager.mu.Lock()
		id := manager.latest.snapshot.ID
		manager.mu.Unlock()
		finished := waitDesktopRecovery(t, manager, id)
		if finished.Status != "repaired" || calls.Load() != int32(i) {
			t.Fatalf("new failure reused old operation: %+v calls=%d", finished, calls.Load())
		}
	}
}

func TestDesktopRecoveryHistoryIsBounded(t *testing.T) {
	manager, _ := isolateDesktopRecovery(t)
	var first string
	for i := 0; i <= desktopRecoveryHistory; i++ {
		accepted, err := StartDesktopRecovery(context.Background(), DesktopRecoveryRequest{RequestID: fmt.Sprintf("history-%d", i)})
		if err != nil {
			t.Fatal(err)
		}
		if i == 0 {
			first = accepted.ID
		}
		waitDesktopRecovery(t, manager, accepted.ID)
	}
	if len(manager.records) != desktopRecoveryHistory || len(manager.requests) != desktopRecoveryHistory || len(manager.order) != desktopRecoveryHistory {
		t.Fatalf("terminal history grew without bound: records=%d requests=%d order=%d", len(manager.records), len(manager.requests), len(manager.order))
	}
	if _, err := GetDesktopRecovery(first); !errors.Is(err, ErrRecoveryNotFound) {
		t.Fatalf("old terminal operation was not evicted: %v", err)
	}
}
