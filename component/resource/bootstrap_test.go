package resource

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/http/httptrace"
	C "github.com/metacubex/mihomo/constant"
)

type resourceBootstrapDialer struct {
	calls atomic.Int32
	err   error
}

func (d *resourceBootstrapDialer) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	d.calls.Add(1)
	if d.err != nil {
		return nil, d.err
	}
	return (&net.Dialer{}).DialContext(ctx, network, address)
}
func (*resourceBootstrapDialer) ListenPacket(context.Context, string, string, netip.AddrPort) (net.PacketConn, error) {
	return nil, errors.New("unexpected UDP")
}

// Functional tests use the test's deadline only as a hang guard. Configured
// deadline behavior is exercised separately, without making fixture setup,
// loopback scheduling or a cache fsync part of a one-second success criterion.
func resourceBootstrapTestContext(t *testing.T) context.Context {
	t.Helper()
	deadline, ok := t.Deadline()
	if !ok {
		deadline = time.Now().Add(time.Minute)
	}
	cleanup := time.Until(deadline) / 10
	if cleanup > time.Second {
		cleanup = time.Second
	}
	ctx, cancel := context.WithDeadline(context.Background(), deadline.Add(-cleanup))
	t.Cleanup(cancel)
	return ctx
}

func resourceBootstrapTestScope(ctx context.Context, dialer C.Dialer) *Bootstrap {
	deadline, _ := ctx.Deadline() // All callers use resourceBootstrapTestContext.
	return NewBootstrap(ctx, time.Until(deadline), dialer, nil)
}

type resourceBootstrapTrace struct {
	start  time.Time
	mu     sync.Mutex
	phases []string
}

func newResourceBootstrapTrace(t *testing.T) *resourceBootstrapTrace {
	trace := &resourceBootstrapTrace{start: time.Now()}
	t.Cleanup(func() {
		trace.mu.Lock()
		defer trace.mu.Unlock()
		t.Logf("bootstrap phases: %s", strings.Join(trace.phases, ", "))
	})
	return trace
}

func (trace *resourceBootstrapTrace) mark(phase string) {
	trace.mu.Lock()
	defer trace.mu.Unlock()
	trace.phases = append(trace.phases, fmt.Sprintf("%s=%s", phase, time.Since(trace.start)))
}

func TestResourceBootstrapBudgetAndDiagnostics(t *testing.T) {
	trace := newResourceBootstrapTrace(t)
	ctx := WithBootstrapTimeout(context.Background(), 35*time.Millisecond)
	b := NewBootstrap(ctx, time.Second, nil, nil)
	defer b.Close()
	trace.mark("budget_started")
	if got := InitialDownloadTimeout(b.Context(), 20*time.Second); got != 60*time.Second {
		t.Fatalf("provider timeout = %s", got)
	}
	if got := InitialDownloadTimeout(WithInitialResource(b.Context(), "geo", "geosite.dat"), 90*time.Second); got != 120*time.Second {
		t.Fatalf("Geo timeout = %s", got)
	}
	if got := InitialDownloadTimeout(context.Background(), 20*time.Second); got != 20*time.Second {
		t.Fatal("runtime timeout changed")
	}
	<-b.Context().Done()
	trace.mark("budget_expired")
	err := BootstrapFailure(WithInitialResource(b.Context(), "provider", "rules"), b.Context().Err())
	if !errors.Is(err, context.DeadlineExceeded) || !strings.Contains(err.Error(), "code=deadline_exceeded kind=provider") {
		t.Fatalf("deadline error = %v", err)
	}
	for _, value := range []string{"0s", "-1s", "NaN", "9999999999999999999s", ""} {
		if _, err := ParseBootstrapTimeout(value); err == nil {
			t.Fatalf("accepted %q", value)
		}
	}
	if value, err := ParseBootstrapTimeout("1.234s"); err != nil || value != 1234*time.Millisecond {
		t.Fatalf("duration = %s %v", value, err)
	}
	err = BootstrapFailure(b.Context(), errors.New("https://user:password@example.invalid/?token=secret"))
	if strings.Contains(err.Error(), "secret") || strings.Contains(err.Error(), "password") {
		t.Fatalf("unsafe diagnostic: %v", err)
	}
}

func TestResourceBootstrapDownloadCacheAndRuntimeIsolation(t *testing.T) {
	trace := newResourceBootstrapTrace(t)
	watchdog := resourceBootstrapTestContext(t)
	var requests atomic.Int32
	releaseRuntime := make(chan struct{})
	releaseRequests := sync.OnceFunc(func() { close(releaseRuntime) })
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if requests.Add(1) > 1 {
			trace.mark("runtime_request_received")
			// A runtime refresh must cancel; a successful response must not race
			// the client's deadline timer when the test runner is busy.
			select {
			case <-r.Context().Done():
			case <-releaseRuntime:
			}
			return
		}
		trace.mark("initial_request_received")
		// This response deliberately outlasts the runtime's 10 ms budget.
		// There is no short upper bound on a successful startup request.
		timer := time.NewTimer(35 * time.Millisecond)
		defer timer.Stop()
		select {
		case <-timer.C:
		case <-r.Context().Done():
			return
		case <-releaseRuntime:
			return
		}
		_, _ = w.Write([]byte("valid rules"))
	}))
	defer server.Close()
	defer releaseRequests()
	path := filepath.Join(t.TempDir(), "rules.txt")
	routed := &resourceBootstrapDialer{}
	// The startup scope overrides the old per-vehicle deadline; later refreshes
	// retain that vehicle's own policy instead of capturing the temporary scope.
	vehicle := NewHTTPVehicle(server.URL, path, "", nil, 10*time.Millisecond, 1024)
	var published atomic.Int32
	parser := func(buf []byte) (string, error) { return string(buf), nil }
	fetcher := NewFetcher("rules", 0, vehicle, nil, parser, func(string) { published.Add(1) })
	defer fetcher.Close()
	trace.mark("fixture_ready")
	scope := resourceBootstrapTestScope(watchdog, routed)
	defer scope.Close()
	trace.mark("bootstrap_started")
	if value, err := fetcher.InitialContext(scope.Context()); err != nil || value != "valid rules" {
		t.Fatalf("initial download = %q %v", value, err)
	}
	trace.mark("initial_returned")
	if routed.calls.Load() != 1 || published.Load() != 1 {
		t.Fatal("download did not use the selected scoped route")
	}
	_ = scope.Close()
	trace.mark("runtime_started")
	updated := make(chan error, 1)
	go func() {
		_, _, err := fetcher.Update()
		updated <- err
	}()
	select {
	case err := <-updated:
		trace.mark("runtime_returned")
		if !errors.Is(err, context.DeadlineExceeded) {
			t.Fatalf("runtime refresh deadline = %v", err)
		}
	case <-watchdog.Done():
		t.Fatal("runtime refresh did not finish before the test watchdog")
	}
	if routed.calls.Load() != 1 {
		t.Fatal("runtime refresh retained the temporary dialer")
	}
	// A timed-out request can reach its handler after the client has returned.
	// Drain those handlers before counting, and make cache verification truly
	// offline instead of mistaking a late runtime request for a cache download.
	releaseRequests()
	server.Close()
	trace.mark("server_closed_for_offline_cache")
	before := requests.Load()
	cached := NewFetcher("cached", 0, vehicle, nil, parser, nil)
	defer cached.Close()
	fresh := resourceBootstrapTestScope(watchdog, routed)
	defer fresh.Close()
	if value, err := cached.InitialContext(fresh.Context()); err != nil || value != "valid rules" {
		t.Fatalf("offline cache = %q %v", value, err)
	}
	if requests.Load() != before {
		t.Fatal("valid cache with interval zero was downloaded again")
	}
	data, err := os.ReadFile(path)
	if err != nil || string(data) != "valid rules" || published.Load() != 1 {
		t.Fatalf("failed runtime update changed state: cache=%q, publications=%d, error=%v", data, published.Load(), err)
	}
	trace.mark("cache_verified")
}

func TestResourceBootstrapExplicitProviderProxyWins(t *testing.T) {
	trace := newResourceBootstrapTrace(t)
	watchdog := resourceBootstrapTestContext(t)
	var requests, invalidRequests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		trace.mark("server_received_request")
		if r.Method != http.MethodGet || r.URL.Path != "/rules" {
			invalidRequests.Add(1)
			http.Error(w, "unexpected request", http.StatusBadRequest)
			return
		}
		_, _ = w.Write([]byte("rules"))
	}))
	defer server.Close()
	trace.mark("server_ready")
	path := filepath.Join(t.TempDir(), "rules")
	trace.mark("cache_directory_ready")
	unexpectedDefault := errors.New("explicit provider used the startup default dialer")
	routed := &resourceBootstrapDialer{err: unexpectedDefault}
	vehicle := NewHTTPVehicle(server.URL+"/rules", path, "DIRECT", nil, time.Second, 100)
	var parsed, published atomic.Int32
	var publishedValue string
	fetcher := NewFetcher("explicit", 0, vehicle, nil, func(data []byte) (string, error) {
		parsed.Add(1)
		trace.mark("response_parsed")
		return string(data), nil
	}, func(value string) {
		published.Add(1)
		publishedValue = value
		// The fetcher invokes onUpdate after the cache write has completed.
		trace.mark("cache_written_and_published")
	})
	defer fetcher.Close()
	trace.mark("fixture_ready")
	ctx := httptrace.WithClientTrace(watchdog, &httptrace.ClientTrace{
		GetConn:              func(string) { trace.mark("connection_requested") },
		GotConn:              func(httptrace.GotConnInfo) { trace.mark("connection_ready") },
		WroteRequest:         func(httptrace.WroteRequestInfo) { trace.mark("request_written") },
		GotFirstResponseByte: func() { trace.mark("response_started") },
	})
	scope := resourceBootstrapTestScope(ctx, routed)
	defer scope.Close()
	trace.mark("bootstrap_started")
	value, err := fetcher.InitialContext(scope.Context())
	trace.mark("initial_returned")
	if calls := routed.calls.Load(); calls != 0 || errors.Is(err, unexpectedDefault) {
		t.Fatalf("startup default replaced explicit provider.proxy: calls=%d, error=%v", calls, err)
	}
	if err != nil || value != "rules" {
		t.Fatalf("explicit DIRECT download = %q %v", value, err)
	}
	if requests.Load() != 1 || invalidRequests.Load() != 0 || parsed.Load() != 1 {
		t.Fatalf("expected one real GET and parse: requests=%d, invalid=%d, parsed=%d", requests.Load(), invalidRequests.Load(), parsed.Load())
	}
	data, err := os.ReadFile(path)
	if err != nil || string(data) != "rules" || published.Load() != 1 || publishedValue != "rules" || fetcher.UpdatedAt().IsZero() {
		t.Fatalf("download was not committed: cache=%q, published=%q, publications=%d, error=%v", data, publishedValue, published.Load(), err)
	}
	trace.mark("cache_verified")
}

func TestResourceBootstrapCancelDoesNotPublishLateContent(t *testing.T) {
	trace := newResourceBootstrapTrace(t)
	watchdog := resourceBootstrapTestContext(t)
	readStarted := make(chan struct{})
	var writes, publishes atomic.Int32
	vehicle := &controlledVehicle{
		read: func(ctx context.Context) ([]byte, error) {
			close(readStarted)
			<-ctx.Done()
			trace.mark("late_content_returned")
			return []byte("late"), nil
		},
		write: func([]byte) error { writes.Add(1); return nil },
	}
	fetcher := NewFetcher("cancelled", 0, vehicle, nil, func(buf []byte) (string, error) { return string(buf), nil }, func(string) { publishes.Add(1) })
	defer fetcher.Close()
	scope := resourceBootstrapTestScope(watchdog, nil)
	defer scope.Close()
	result := make(chan error, 1)
	go func() {
		_, err := fetcher.InitialContext(scope.Context())
		result <- err
	}()
	select {
	case <-readStarted:
		trace.mark("read_started")
	case <-watchdog.Done():
		t.Fatal("initial read did not start before the test watchdog")
	}
	_ = scope.Close()
	trace.mark("scope_closed")
	select {
	case err := <-result:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("late response = %v", err)
		}
	case <-watchdog.Done():
		t.Fatal("cancelled initial read did not return before the test watchdog")
	}
	if writes.Load() != 0 || publishes.Load() != 0 || !fetcher.UpdatedAt().IsZero() {
		t.Fatal("cancelled resource was committed")
	}
}

func TestResourceBootstrapBackgroundRefreshHasIndependentContext(t *testing.T) {
	trace := newResourceBootstrapTrace(t)
	watchdog := resourceBootstrapTestContext(t)
	type observation struct {
		bootstrap bool
		err       error
	}
	initialSeen, runtimeSeen := make(chan observation, 1), make(chan observation, 1)
	scopeClosed := make(chan struct{})
	vehicle := &controlledVehicle{
		read: func(ctx context.Context) ([]byte, error) {
			seen := initialSeen
			select {
			case <-scopeClosed:
				seen = runtimeSeen
			default:
			}
			// Observation must never stop the refresh loop or its cleanup if
			// the test goroutine is descheduled while several refreshes run.
			select {
			case seen <- observation{BootstrapEnabled(ctx), ctx.Err()}:
			default:
			}
			return []byte("value"), nil
		},
		write: func([]byte) error { return nil },
	}
	fetcher := NewFetcher("background", 20*time.Millisecond, vehicle, nil, func(buf []byte) (string, error) { return string(buf), nil }, nil)
	defer fetcher.Close()
	scope := resourceBootstrapTestScope(watchdog, nil)
	defer scope.Close()
	if _, err := fetcher.InitialContext(scope.Context()); err != nil {
		t.Fatal(err)
	}
	select {
	case got := <-initialSeen:
		if !got.bootstrap || got.err != nil {
			t.Fatalf("initial request context: bootstrap=%v, error=%v", got.bootstrap, got.err)
		}
	default:
		t.Fatal("initial request did not read the vehicle")
	}
	_ = scope.Close()
	close(scopeClosed)
	trace.mark("scope_closed")
	select {
	case got := <-runtimeSeen:
		trace.mark("refresh_after_close")
		if got.bootstrap || got.err != nil {
			t.Fatalf("scheduled refresh context: bootstrap=%v, error=%v", got.bootstrap, got.err)
		}
	case <-watchdog.Done():
		t.Fatal("closing startup scope stopped normal provider refresh")
	}
}
