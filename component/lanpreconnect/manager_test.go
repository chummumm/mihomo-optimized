package lanpreconnect

import (
	"context"
	"net/netip"
	"sync/atomic"
	"testing"
	"time"
)

type testPreparer struct {
	name    string
	prepare func(context.Context, netip.Addr) error
	path    string
}

func (p *testPreparer) Name() string { return p.name }
func (p *testPreparer) PrepareLAN(ctx context.Context, target netip.Addr) error {
	return p.prepare(ctx, target)
}
func (p *testPreparer) LANPreconnectKey(netip.Addr) string { return p.path }

func await[T any](t *testing.T, ch <-chan T) T {
	t.Helper()
	select {
	case result := <-ch:
		return result
	case <-time.After(2 * time.Second):
		t.Fatal("background operation did not finish")
		var zero T
		return zero
	}
}

func TestLANScope(t *testing.T) {
	for _, value := range []string{"10.0.0.1", "172.16.0.1", "172.31.255.254", "192.168.42.7", "fd12::1", "fc00::7", "::ffff:192.168.42.7"} {
		if !IsLAN(netip.MustParseAddr(value)) {
			t.Errorf("LAN excluded: %s", value)
		}
	}
	for _, value := range []string{"100.64.0.1", "1.1.1.1", "172.15.0.1", "172.32.0.1", "169.254.1.1", "127.0.0.1", "0.0.0.0", "224.0.0.1", "fe80::1", "::1", "::", "ff02::1", "2001:db8::1", "::ffff:100.64.0.1"} {
		if IsLAN(netip.MustParseAddr(value)) {
			t.Errorf("non-LAN admitted: %s", value)
		}
	}
}

func TestLANStaticIntentPreparesBeforeBusinessAndStopIsPermanent(t *testing.T) {
	m := New()
	defer m.Close()
	started := make(chan netip.Addr, 4)
	stopped := make(chan struct{}, 4)
	p := &testPreparer{name: "lan", prepare: func(ctx context.Context, target netip.Addr) error {
		started <- target
		<-ctx.Done()
		stopped <- struct{}{}
		return ctx.Err()
	}}
	ip := netip.MustParseAddr("192.168.42.5")
	m.Observe(p, ip)
	m.mu.Lock()
	if len(m.entries) != 0 {
		t.Error("observation started an unapplied configuration")
	}
	m.mu.Unlock()
	m.Replace([]Intent{{Outbound: p, Target: ip}})
	if got := await(t, started); got != ip {
		t.Fatalf("target = %v", got)
	}
	m.Stop()
	await(t, stopped)
	m.Replace([]Intent{{Outbound: p, Target: ip}})
	m.Observe(p, ip)
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.enabled || len(m.entries) != 0 {
		t.Fatal("late apply revived stopped coordinator")
	}
}

func TestLANGenerationCancellationKeepsInstanceSlotUntilOldWorkExits(t *testing.T) {
	m := New()
	defer m.Close()
	started := make(chan netip.Addr, 4)
	canceled := make(chan struct{}, 4)
	release := make(chan struct{})
	var calls atomic.Int32
	p := &testPreparer{name: "lan", prepare: func(ctx context.Context, target netip.Addr) error {
		n := calls.Add(1)
		started <- target
		if n == 1 {
			<-ctx.Done()
			canceled <- struct{}{}
			<-release  // emulate a native cancellation acknowledgement in progress
			return nil // an obsolete success must not complete the replacement job
		}
		return nil
	}}
	first, second := netip.MustParseAddr("192.168.1.2"), netip.MustParseAddr("192.168.2.3")
	m.Replace([]Intent{{Outbound: p, Target: first}})
	await(t, started)
	m.Replace([]Intent{{Outbound: p, Target: second}})
	await(t, canceled)
	if calls.Load() != 1 {
		t.Fatal("same instance prepared concurrently across generations")
	}
	close(release)
	if got := await(t, started); got != second {
		t.Fatalf("replacement target = %v", got)
	}
	if calls.Load() != 2 {
		t.Fatal("obsolete success suppressed replacement work")
	}
}

func TestLANCoordinatorBoundsMemoryAndConcurrency(t *testing.T) {
	m := New()
	defer m.Close()
	started := make(chan struct{}, 256)
	var active, peak atomic.Int32
	prepare := func(ctx context.Context, _ netip.Addr) error {
		n := active.Add(1)
		for old := peak.Load(); n > old && !peak.CompareAndSwap(old, n); old = peak.Load() {
		}
		started <- struct{}{}
		<-ctx.Done()
		active.Add(-1)
		return ctx.Err()
	}
	m.Replace(nil)
	for i := 0; i < maxIntents+20; i++ {
		p := &testPreparer{name: "lan", prepare: prepare}
		m.Observe(p, netip.AddrFrom4([4]byte{192, 168, byte(i), 1}))
	}
	await(t, started)
	await(t, started)
	m.mu.Lock()
	if len(m.entries) > maxIntents {
		t.Errorf("unbounded entries: %d", len(m.entries))
	}
	if len(m.busy) > workers {
		t.Errorf("unbounded active instances: %d", len(m.busy))
	}
	m.mu.Unlock()
	if peak.Load() > workers {
		t.Fatalf("native preparation concurrency = %d", peak.Load())
	}
	m.Suspend()
	m.mu.Lock()
	if len(m.entries) != 0 {
		t.Error("suspend retained old intents")
	}
	m.mu.Unlock()
}

func TestLANSameNativePathIsPreparedOnceAndInvalidated(t *testing.T) {
	m := New()
	defer m.Close()
	finished := make(chan struct{}, 4)
	var calls atomic.Int32
	p := &testPreparer{name: "gateway", path: "public-key", prepare: func(context.Context, netip.Addr) error {
		calls.Add(1)
		finished <- struct{}{}
		return nil
	}}
	m.Replace([]Intent{{p, netip.MustParseAddr("10.1.0.1")}, {p, netip.MustParseAddr("10.2.0.2")}})
	await(t, finished)
	deadline := time.Now().Add(time.Second)
	for {
		m.mu.Lock()
		pending := false
		for _, entry := range m.entries {
			pending = pending || entry.pending
		}
		m.mu.Unlock()
		if !pending {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("path deduplication did not finish")
		}
		time.Sleep(time.Millisecond)
	}
	if calls.Load() != 1 {
		t.Fatalf("same gateway prepared %d times", calls.Load())
	}
	m.Changed(p)
	await(t, finished)
	if calls.Load() != 2 {
		t.Fatal("route change did not invalidate gateway result")
	}
}

func TestLANBusinessSelectionCannotBeReplacedByNestedEndpoint(t *testing.T) {
	p, q := &testPreparer{name: "business"}, &testPreparer{name: "underlay"}
	if NeedsSelection(context.Background()) {
		t.Fatal("health test treated as business")
	}
	ctx := Business(context.Background())
	ctx = Select(ctx, p)
	ctx = Select(ctx, q)
	if !Selected(ctx, p) || Selected(ctx, q) || NeedsSelection(ctx) {
		t.Fatal("nested dial changed actual destination owner")
	}
	ctx = Select(Business(context.Background()), nil)
	ctx = Select(ctx, q)
	if Selected(ctx, q) || NeedsSelection(ctx) {
		t.Fatal("normal proxy endpoint mistaken for LAN business target")
	}
}
