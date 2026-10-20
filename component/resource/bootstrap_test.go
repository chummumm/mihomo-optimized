package resource

import (
	"context"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

type resourceBootstrapDialer struct{ calls atomic.Int32 }

func (d *resourceBootstrapDialer) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	d.calls.Add(1)
	return (&net.Dialer{}).DialContext(ctx, network, address)
}
func (*resourceBootstrapDialer) ListenPacket(context.Context, string, string, netip.AddrPort) (net.PacketConn, error) {
	return nil, errors.New("unexpected UDP")
}

func TestResourceBootstrapBudgetAndDiagnostics(t *testing.T) {
	ctx := WithBootstrapTimeout(context.Background(), 35*time.Millisecond)
	b := NewBootstrap(ctx, time.Second, nil, nil)
	defer b.Close()
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
	var requests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		requests.Add(1)
		time.Sleep(35 * time.Millisecond)
		_, _ = w.Write([]byte("valid rules"))
	}))
	defer server.Close()
	path := filepath.Join(t.TempDir(), "rules.txt")
	routed := &resourceBootstrapDialer{}
	scope := NewBootstrap(context.Background(), time.Second, routed, nil)
	defer scope.Close()
	// The startup scope overrides the old per-vehicle deadline; later refreshes
	// retain that vehicle's own policy instead of capturing the temporary scope.
	vehicle := NewHTTPVehicle(server.URL, path, "", nil, 10*time.Millisecond, 1024)
	var published atomic.Int32
	parser := func(buf []byte) (string, error) { return string(buf), nil }
	fetcher := NewFetcher("rules", 0, vehicle, nil, parser, func(string) { published.Add(1) })
	defer fetcher.Close()
	if value, err := fetcher.InitialContext(scope.Context()); err != nil || value != "valid rules" {
		t.Fatalf("initial download = %q %v", value, err)
	}
	if routed.calls.Load() != 1 || published.Load() != 1 {
		t.Fatal("download did not use the selected scoped route")
	}
	_ = scope.Close()
	if _, _, err := fetcher.Update(); err == nil {
		t.Fatal("runtime refresh inherited the larger startup timeout")
	}
	if routed.calls.Load() != 1 {
		t.Fatal("runtime refresh retained the temporary dialer")
	}
	before := requests.Load()
	cached := NewFetcher("cached", 0, vehicle, nil, parser, nil)
	defer cached.Close()
	fresh := NewBootstrap(context.Background(), time.Second, routed, nil)
	defer fresh.Close()
	if value, err := cached.InitialContext(fresh.Context()); err != nil || value != "valid rules" {
		t.Fatalf("offline cache = %q %v", value, err)
	}
	if requests.Load() != before {
		t.Fatal("valid cache with interval zero was downloaded again")
	}
	data, _ := os.ReadFile(path)
	if string(data) != "valid rules" {
		t.Fatal("failed runtime update changed the cache")
	}
}

func TestResourceBootstrapExplicitProviderProxyWins(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("rules")) }))
	defer server.Close()
	routed := &resourceBootstrapDialer{}
	scope := NewBootstrap(context.Background(), time.Second, routed, nil)
	defer scope.Close()
	vehicle := NewHTTPVehicle(server.URL, filepath.Join(t.TempDir(), "rules"), "DIRECT", nil, time.Second, 100)
	fetcher := NewFetcher("explicit", 0, vehicle, nil, func(data []byte) (string, error) { return string(data), nil }, nil)
	defer fetcher.Close()
	if _, err := fetcher.InitialContext(scope.Context()); err != nil {
		t.Fatal(err)
	}
	if routed.calls.Load() != 0 {
		t.Fatal("startup default replaced explicit provider.proxy")
	}
}

func TestResourceBootstrapCancelDoesNotPublishLateContent(t *testing.T) {
	scope := NewBootstrap(context.Background(), time.Second, nil, nil)
	defer scope.Close()
	var writes, publishes atomic.Int32
	vehicle := &controlledVehicle{
		read:  func(context.Context) ([]byte, error) { _ = scope.Close(); return []byte("late"), nil },
		write: func([]byte) error { writes.Add(1); return nil },
	}
	fetcher := NewFetcher("cancelled", 0, vehicle, nil, func(buf []byte) (string, error) { return string(buf), nil }, func(string) { publishes.Add(1) })
	defer fetcher.Close()
	if _, err := fetcher.InitialContext(scope.Context()); !errors.Is(err, context.Canceled) {
		t.Fatalf("late response = %v", err)
	}
	if writes.Load() != 0 || publishes.Load() != 0 || !fetcher.UpdatedAt().IsZero() {
		t.Fatal("cancelled resource was committed")
	}
}

func TestResourceBootstrapBackgroundRefreshHasIndependentContext(t *testing.T) {
	seen := make(chan bool, 8)
	vehicle := &controlledVehicle{
		read:  func(ctx context.Context) ([]byte, error) { seen <- BootstrapEnabled(ctx); return []byte("value"), nil },
		write: func([]byte) error { return nil },
	}
	fetcher := NewFetcher("background", 20*time.Millisecond, vehicle, nil, func(buf []byte) (string, error) { return string(buf), nil }, nil)
	defer fetcher.Close()
	scope := NewBootstrap(context.Background(), time.Second, nil, nil)
	defer scope.Close()
	if _, err := fetcher.InitialContext(scope.Context()); err != nil {
		t.Fatal(err)
	}
	if !<-seen {
		t.Fatal("initial request lacked startup scope")
	}
	_ = scope.Close()
	select {
	case initial := <-seen:
		if initial {
			t.Fatal("scheduled refresh retained startup scope")
		}
	case <-time.After(time.Second):
		t.Fatal("closing startup scope stopped normal provider refresh")
	}
}
