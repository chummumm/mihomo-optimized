package tun

import (
	"errors"
	"net/netip"
	"os"
	"strings"
	"testing"

	"github.com/metacubex/sing-tun/internal/wintun"
)

type failingOwnedAdapter struct {
	closed int
	err    error
}

func (a *failingOwnedAdapter) StartSession(uint32) (wintun.Session, error) {
	return wintun.Session{}, a.err
}
func (a *failingOwnedAdapter) Close() error { a.closed++; return nil }
func (*failingOwnedAdapter) LUID() uint64   { return 123 }

func TestDesktopExclusiveAdapterNeverOpensExisting(t *testing.T) {
	original := createWindowsAdapter
	t.Cleanup(func() { createWindowsAdapter = original })
	for _, failure := range []error{os.ErrExist, os.ErrPermission, errors.New("driver unavailable")} {
		attempts := 0
		createWindowsAdapter = func(Options) (windowsAdapter, error) { attempts++; return nil, failure }
		device, err := NewExclusive(Options{Name: "foreign-adapter"})
		if device != nil || !errors.Is(err, failure) || attempts != 1 {
			t.Fatalf("failed create must return directly: device=%v err=%v attempts=%d", device, err, attempts)
		}
	}
}

func TestDesktopFailedSessionClosesOwnedAdapterBeforeRetry(t *testing.T) {
	original := createWindowsAdapter
	t.Cleanup(func() { createWindowsAdapter = original })
	failure := errors.New("driver session failure")
	previous := &failingOwnedAdapter{err: failure}
	createWindowsAdapter = func(Options) (windowsAdapter, error) { return previous, nil }
	if device, err := NewExclusive(Options{Name: "owned-adapter"}); device != nil || !errors.Is(err, failure) || previous.closed != 1 {
		t.Fatalf("session failure did not release ownership: device=%v err=%v closes=%d", device, err, previous.closed)
	}
	createWindowsAdapter = func(Options) (windowsAdapter, error) {
		if previous.closed != 1 {
			t.Fatal("next create began before the prior adapter closed")
		}
		return nil, os.ErrExist
	}
	if _, err := NewExclusive(Options{Name: "owned-adapter"}); !errors.Is(err, os.ErrExist) {
		t.Fatal(err)
	}
}

func TestDesktopWindowsRouteFailureCannotBeOverwrittenByLaterSuccess(t *testing.T) {
	options := Options{
		AutoRoute:         true,
		Inet4Address:      []netip.Prefix{netip.MustParsePrefix("172.19.0.1/30")},
		Inet6Address:      []netip.Prefix{netip.MustParsePrefix("fd00::1/126")},
		Inet4RouteAddress: []netip.Prefix{netip.MustParsePrefix("0.0.0.0/1"), netip.MustParsePrefix("128.0.0.0/1")},
	}
	routes, err := options.BuildAutoRouteRanges(false)
	if err != nil || len(routes) != 3 {
		t.Fatalf("invalid test routes: %v %v", routes, err)
	}
	failure := errors.New("route creation failed")
	for _, failedAttempt := range []int{1, 2} {
		attempts := 0
		err := configureWindowsRoutes(options, func(prefix netip.Prefix, gateway netip.Addr, metric uint32) error {
			attempts++
			if attempts == failedAttempt {
				return failure
			}
			return nil
		})
		if !errors.Is(err, failure) || attempts != failedAttempt || !strings.Contains(err.Error(), routes[failedAttempt-1].String()) {
			t.Fatalf("partial route failure was lost: failure_at=%d attempts=%d err=%v", failedAttempt, attempts, err)
		}
	}
	attempts := 0
	if err := configureWindowsRoutes(options, func(prefix netip.Prefix, gateway netip.Addr, metric uint32) error {
		if prefix != routes[attempts] || metric != 0 {
			t.Fatalf("unexpected route %s metric=%d", prefix, metric)
		}
		expectedGateway := netip.MustParseAddr("fd00::2")
		if prefix.Addr().Is4() {
			expectedGateway = netip.MustParseAddr("172.19.0.2")
		}
		if gateway != expectedGateway {
			t.Fatalf("route %s used gateway %s instead of %s", prefix, gateway, expectedGateway)
		}
		attempts++
		return nil
	}); err != nil || attempts != len(routes) {
		t.Fatalf("complete route configuration failed: attempts=%d err=%v", attempts, err)
	}
}
