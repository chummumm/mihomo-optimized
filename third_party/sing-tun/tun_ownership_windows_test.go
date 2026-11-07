package tun

import (
	"errors"
	"net/netip"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/metacubex/sing-tun/internal/wintun"
	"golang.org/x/sys/windows"
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

func TestDesktopExclusiveAdapterChecksAliasBeforeCreate(t *testing.T) {
	originalCreate, originalAliases := createExclusiveWindowsAdapter, listWindowsInterfaceAliases
	originalProof := canReenumerateWindowsAlias
	t.Cleanup(func() {
		createExclusiveWindowsAdapter = originalCreate
		listWindowsInterfaceAliases = originalAliases
		canReenumerateWindowsAlias = originalProof
	})
	canReenumerateWindowsAlias = func(string) (bool, error) { return false, nil }
	lookupFailure := errors.New("interface enumeration failed")
	createFailure := errors.New("driver create reached")
	for _, test := range []struct {
		name         string
		aliases      []string
		lookupErr    error
		wantErr      error
		wantAttempts int
		wantWait     time.Duration
	}{
		{name: "existing alias", aliases: []string{"Ethernet", "owned-adapter"}, wantErr: os.ErrExist, wantWait: 500 * time.Millisecond},
		{name: "case-insensitive alias", aliases: []string{"OWNED-Adapter"}, wantErr: os.ErrExist, wantWait: 500 * time.Millisecond},
		{name: "lookup failure", lookupErr: lookupFailure, wantErr: lookupFailure},
		{name: "alias absent", aliases: []string{"Ethernet", "another-adapter"}, wantErr: createFailure, wantAttempts: 1},
		{name: "empty interface table", wantErr: createFailure, wantAttempts: 1},
	} {
		t.Run(test.name, func(t *testing.T) {
			lookedUp, attempts := false, 0
			started := time.Unix(0, 0)
			current := started
			listWindowsInterfaceAliases = func() ([]string, error) {
				lookedUp = true
				return test.aliases, test.lookupErr
			}
			createExclusiveWindowsAdapter = func(options Options) (windowsAdapter, error) {
				attempts++
				if !lookedUp || options.Name != "owned-adapter" {
					t.Fatal("adapter creation preceded the alias check or changed the requested name")
				}
				return nil, createFailure
			}
			device, err := newWindowsExclusive(Options{Name: "owned-adapter"}, func() time.Time { return current }, func(delay time.Duration) {
				if delay <= 0 || delay > 50*time.Millisecond || current.Sub(started)+delay > 500*time.Millisecond {
					t.Fatalf("alias grace exceeded its fixed deadline: elapsed=%s delay=%s", current.Sub(started), delay)
				}
				current = current.Add(delay)
			})
			if device != nil || !lookedUp || !errors.Is(err, test.wantErr) || attempts != test.wantAttempts || current.Sub(started) != test.wantWait {
				t.Fatalf("unexpected alias guard result: device=%v err=%v attempts=%d lookup=%v waited=%s", device, err, attempts, lookedUp, current.Sub(started))
			}
		})
	}
}

func TestDesktopExclusiveAdapterWaitsForAliasRemoval(t *testing.T) {
	originalCreate, originalAliases := createExclusiveWindowsAdapter, listWindowsInterfaceAliases
	originalProof := canReenumerateWindowsAlias
	t.Cleanup(func() {
		createExclusiveWindowsAdapter = originalCreate
		listWindowsInterfaceAliases = originalAliases
		canReenumerateWindowsAlias = originalProof
	})
	canReenumerateWindowsAlias = func(string) (bool, error) { return false, nil }
	lookupFailure := errors.New("interface enumeration failed during removal")
	createFailure := errors.New("driver create reached")
	for _, test := range []struct {
		name         string
		lookupErr    error
		wantErr      error
		wantAttempts int
	}{
		{name: "alias disappears", wantErr: createFailure, wantAttempts: 1},
		{name: "lookup fails during grace", lookupErr: lookupFailure, wantErr: lookupFailure},
	} {
		t.Run(test.name, func(t *testing.T) {
			current := time.Unix(0, 0)
			lookups, attempts := 0, 0
			listWindowsInterfaceAliases = func() ([]string, error) {
				lookups++
				if lookups == 1 {
					return []string{"owned-adapter"}, nil
				}
				return []string{"unrelated-adapter"}, test.lookupErr
			}
			createExclusiveWindowsAdapter = func(Options) (windowsAdapter, error) {
				attempts++
				if lookups != 2 {
					t.Fatal("adapter creation began before confirmed alias removal")
				}
				return nil, createFailure
			}
			device, err := newWindowsExclusive(Options{Name: "owned-adapter"}, func() time.Time { return current }, func(delay time.Duration) {
				current = current.Add(delay)
			})
			if device != nil || !errors.Is(err, test.wantErr) || attempts != test.wantAttempts || lookups != 2 || current.Sub(time.Unix(0, 0)) != 50*time.Millisecond {
				t.Fatalf("unexpected alias removal result: device=%v err=%v attempts=%d lookups=%d elapsed=%s", device, err, attempts, lookups, current.Sub(time.Unix(0, 0)))
			}
		})
	}
}

func TestDesktopExclusiveAdapterNeverOpensExisting(t *testing.T) {
	originalCreate, originalAliases := createExclusiveWindowsAdapter, listWindowsInterfaceAliases
	originalProof := canReenumerateWindowsAlias
	t.Cleanup(func() {
		createExclusiveWindowsAdapter = originalCreate
		listWindowsInterfaceAliases = originalAliases
		canReenumerateWindowsAlias = originalProof
	})
	canReenumerateWindowsAlias = func(string) (bool, error) { return false, nil }
	listWindowsInterfaceAliases = func() ([]string, error) { return nil, nil }
	for _, failure := range []error{os.ErrExist, os.ErrPermission, errors.New("driver unavailable")} {
		attempts := 0
		createExclusiveWindowsAdapter = func(Options) (windowsAdapter, error) { attempts++; return nil, failure }
		device, err := NewExclusive(Options{Name: "foreign-adapter"})
		if device != nil || !errors.Is(err, failure) || attempts != 1 {
			t.Fatalf("failed create must return directly: device=%v err=%v attempts=%d", device, err, attempts)
		}
	}
}

func TestDesktopExclusiveAdapterReenumeratesOnlyVerifiedPhantom(t *testing.T) {
	originalCreate, originalAliases, originalProof := createExclusiveWindowsAdapter, listWindowsInterfaceAliases, canReenumerateWindowsAlias
	t.Cleanup(func() {
		createExclusiveWindowsAdapter, listWindowsInterfaceAliases, canReenumerateWindowsAlias = originalCreate, originalAliases, originalProof
	})
	listWindowsInterfaceAliases = func() ([]string, error) { return []string{"owned-adapter"}, nil }
	proofFailure := errors.New("native identity query failed")
	createFailure := errors.New("owned instance create reached")
	for _, test := range []struct {
		name      string
		verified  bool
		proofErr  error
		wantErr   error
		wantCalls int
	}{
		{name: "verified phantom", verified: true, wantErr: createFailure, wantCalls: 1},
		{name: "query uncertainty", proofErr: proofFailure, wantErr: proofFailure},
	} {
		t.Run(test.name, func(t *testing.T) {
			proofs, calls := 0, 0
			canReenumerateWindowsAlias = func(name string) (bool, error) {
				proofs++
				if name != "owned-adapter" {
					t.Fatalf("unexpected identity target %q", name)
				}
				return test.verified, test.proofErr
			}
			createExclusiveWindowsAdapter = func(Options) (windowsAdapter, error) {
				calls++
				if proofs != 1 || !test.verified {
					t.Fatal("creation preceded definitive native identity proof")
				}
				return nil, createFailure
			}
			device, err := newWindowsExclusive(Options{Name: "owned-adapter"}, time.Now, func(time.Duration) {
				t.Fatal("verified phantom or failed query must not restart the grace period")
			})
			if device != nil || !errors.Is(err, test.wantErr) || calls != test.wantCalls || proofs != 1 {
				t.Fatalf("unexpected recovery admission: device=%v err=%v creates=%d proofs=%d", device, err, calls, proofs)
			}
			if test.proofErr != nil && !errors.Is(err, os.ErrExist) {
				t.Fatalf("unverified existing alias must retain conflict semantics: %v", err)
			}
		})
	}
}

func TestDesktopExclusiveAdapterIdentityRejectsForeignAndPresentDevices(t *testing.T) {
	name := "MihomoOwnershipTest"
	guid := *generateExclusiveGUIDByDeviceName(name)
	if guid != *generateExclusiveGUIDByDeviceName(name) || guid == *generateGUIDByDeviceName(name) ||
		guid == *generateExclusiveGUIDByDeviceName(name + "other") {
		t.Fatal("exclusive identity must be stable and separate from the generic sing-tun namespace")
	}
	owned := windowsAdapterIdentity{
		InstanceID:   exclusiveWindowsInstanceID(guid),
		GUID:         guid,
		Name:         name,
		TunnelType:   exclusiveWindowsTunnelType,
		FriendlyName: exclusiveWindowsTunnelType + " Tunnel",
		HardwareIDs:  []string{"Wintun"},
		ClassGUID:    windowsNetworkClass,
		Phantom:      true,
	}
	if !owned.permitsReenumeration(name, guid) {
		t.Fatal("exact product instance with native non-present proof was rejected")
	}
	for _, test := range []struct {
		name   string
		change func(*windowsAdapterIdentity)
	}{
		{"present including down or problem state", func(i *windowsAdapterIdentity) { i.Phantom = false }},
		{"different actual GUID", func(i *windowsAdapterIdentity) { i.GUID = *generateGUIDByDeviceName(name) }},
		{"different instance", func(i *windowsAdapterIdentity) { i.InstanceID += "-other" }},
		{"different alias", func(i *windowsAdapterIdentity) { i.Name += "-other" }},
		{"legacy generic product", func(i *windowsAdapterIdentity) { i.TunnelType = "Meta" }},
		{"changed native marker", func(i *windowsAdapterIdentity) { i.FriendlyName = "Foreign Tunnel" }},
		{"different hardware", func(i *windowsAdapterIdentity) { i.HardwareIDs = []string{"WireGuard"} }},
		{"ambiguous hardware", func(i *windowsAdapterIdentity) { i.HardwareIDs = []string{"Wintun", "other"} }},
		{"different device class", func(i *windowsAdapterIdentity) { i.ClassGUID = windows.GUID{} }},
	} {
		t.Run(test.name, func(t *testing.T) {
			candidate := owned
			test.change(&candidate)
			if candidate.permitsReenumeration(name, guid) {
				t.Fatalf("uncertain or foreign identity admitted: %+v", candidate)
			}
		})
	}
}

func TestDesktopFailedSessionClosesOwnedAdapterBeforeRetry(t *testing.T) {
	originalCreate, originalAliases := createExclusiveWindowsAdapter, listWindowsInterfaceAliases
	originalProof := canReenumerateWindowsAlias
	t.Cleanup(func() {
		createExclusiveWindowsAdapter = originalCreate
		listWindowsInterfaceAliases = originalAliases
		canReenumerateWindowsAlias = originalProof
	})
	canReenumerateWindowsAlias = func(string) (bool, error) { return false, nil }
	listWindowsInterfaceAliases = func() ([]string, error) { return nil, nil }
	failure := errors.New("driver session failure")
	previous := &failingOwnedAdapter{err: failure}
	createExclusiveWindowsAdapter = func(Options) (windowsAdapter, error) { return previous, nil }
	if device, err := NewExclusive(Options{Name: "owned-adapter"}); device != nil || !errors.Is(err, failure) || previous.closed != 1 {
		t.Fatalf("session failure did not release ownership: device=%v err=%v closes=%d", device, err, previous.closed)
	}
	createExclusiveWindowsAdapter = func(Options) (windowsAdapter, error) {
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
