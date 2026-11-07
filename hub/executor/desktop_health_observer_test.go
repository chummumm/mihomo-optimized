package executor

import (
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func waitDesktopObserver(t *testing.T, events <-chan struct{}) {
	t.Helper()
	select {
	case <-events:
	case <-time.After(2 * time.Second):
		t.Fatal("health observer did not reach its synchronization point")
	}
}

func TestDesktopHealthObserverKeepsIdleSnapshotsFreshBeyondClientPoll(t *testing.T) {
	requests, ticks := make(chan struct{}, 1), make(chan time.Time, 1)
	observed, done := make(chan struct{}, 1), make(chan struct{})
	var clock, lastObservation atomic.Int64
	var stopped atomic.Bool
	base := time.Now()
	now := func() time.Time { return base.Add(time.Duration(clock.Load())) }
	go func() {
		runDesktopHealthObserver(requests, ticks, now, func() bool {
			lastObservation.Store(clock.Load())
			observed <- struct{}{}
			return true
		}, stopped.Load)
		close(done)
	}()
	t.Cleanup(func() {
		stopped.Store(true)
		select {
		case requests <- struct{}{}:
		default:
		}
		waitDesktopObserver(t, done)
	})
	// Only this first request activates sampling. The desktop then remains idle
	// for a full 10-second poll interval, longer than the 5-second health TTL.
	requests <- struct{}{}
	waitDesktopObserver(t, observed)
	for elapsed := desktopHealthInterval; elapsed <= 12*time.Second; elapsed += desktopHealthInterval {
		clock.Store(int64(elapsed))
		ticks <- now()
		waitDesktopObserver(t, observed)
		clock.Store(int64(elapsed + time.Second))
		age := time.Duration(clock.Load() - lastObservation.Load())
		if age > 5*time.Second {
			t.Fatalf("healthy idle snapshot expired without another HTTP request: elapsed=%s age=%s", elapsed, age)
		}
	}
}

func TestDesktopHealthObserverCoalescesWhileNativeProbeStalls(t *testing.T) {
	requests, ticks := make(chan struct{}, 1), make(chan time.Time, 1)
	first, blocked, release, done := make(chan struct{}), make(chan struct{}), make(chan struct{}), make(chan struct{})
	var clock, lastObservation atomic.Int64
	var calls, active, overlap atomic.Int32
	var stopped atomic.Bool
	var releaseOnce sync.Once
	base := time.Now()
	now := func() time.Time { return base.Add(time.Duration(clock.Load())) }
	go func() {
		runDesktopHealthObserver(requests, ticks, now, func() bool {
			if active.Add(1) != 1 {
				overlap.Add(1)
			}
			defer active.Add(-1)
			switch calls.Add(1) {
			case 1:
				lastObservation.Store(clock.Load())
				close(first)
			case 2:
				close(blocked)
				<-release
				lastObservation.Store(clock.Load())
			}
			return true
		}, stopped.Load)
		close(done)
	}()
	t.Cleanup(func() {
		stopped.Store(true)
		releaseOnce.Do(func() { close(release) })
		select {
		case requests <- struct{}{}:
		default:
		}
		waitDesktopObserver(t, done)
	})
	requests <- struct{}{}
	waitDesktopObserver(t, first)
	clock.Store(int64(desktopHealthInterval))
	ticks <- now()
	waitDesktopObserver(t, blocked)
	// Neither time nor a burst of readers may refresh the timestamp or create
	// another OS probe while the one retained native call is blocked.
	for i := 0; i < 100; i++ {
		clock.Store(int64(8 * time.Second))
		select {
		case requests <- struct{}{}:
		default:
		}
		select {
		case ticks <- now():
		default:
		}
	}
	if age := time.Duration(clock.Load() - lastObservation.Load()); age <= 5*time.Second || calls.Load() != 2 || overlap.Load() != 0 {
		t.Fatalf("stalled native probe became fresh or overlapped: age=%s calls=%d overlap=%d", age, calls.Load(), overlap.Load())
	}
	stopped.Store(true)
	releaseOnce.Do(func() { close(release) })
	waitDesktopObserver(t, done)
	if calls.Load() != 2 || overlap.Load() != 0 {
		t.Fatalf("queued ticks ran after shutdown: calls=%d overlap=%d", calls.Load(), overlap.Load())
	}
}
