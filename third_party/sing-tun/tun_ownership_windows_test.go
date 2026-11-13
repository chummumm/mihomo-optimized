package tun

import (
	"errors"
	"net/netip"
	"os"
	"strings"
	"syscall"
	"testing"
	"time"

	"github.com/metacubex/sing-tun/internal/wintun"
	"golang.org/x/sys/windows"
)

type failingOwnedAdapter struct {
	closed         int
	readAfterClose int
	err            error
}

func (a *failingOwnedAdapter) StartSession(uint32) (wintun.Session, error) {
	return wintun.Session{}, a.err
}
func (a *failingOwnedAdapter) Close() error { a.closed++; return nil }
func (a *failingOwnedAdapter) LUID() uint64 {
	if a.closed != 0 {
		a.readAfterClose++
	}
	return 123
}

func desktopOwnedIdentity(name string) windowsAdapterIdentity {
	guid := *generateExclusiveGUIDByDeviceName(name)
	return windowsAdapterIdentity{
		// A valid product instance need not use our guessed PnP spelling.
		InstanceID:   `SWD\Wintun\decorated-` + guid.String(),
		GUID:         guid,
		Name:         name,
		TunnelType:   exclusiveWindowsTunnelType,
		FriendlyName: exclusiveWindowsTunnelType + " Tunnel",
		HardwareIDs:  []string{"Wintun"},
		ClassGUID:    windowsNetworkClass,
		StatusKnown:  true,
	}
}

func TestDesktopExclusiveAdapterUsesNativeOwnershipForVerifiedProduct(t *testing.T) {
	originalCreate, originalInspect := createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter
	t.Cleanup(func() {
		createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter = originalCreate, originalInspect
	})
	for _, name := range []string{"MihomoDesktop", "utun"} {
		for _, state := range []string{"present", "down", "problem", "phantom", "renamed-alias"} {
			t.Run(name+"/"+state, func(t *testing.T) {
				identity := desktopOwnedIdentity(name)
				row := windowsInterfaceIdentity{Alias: name, GUID: identity.GUID, LUID: 123, Index: 18, OperStatus: 1, AdminState: 1}
				switch state {
				case "down":
					row.OperStatus = 2
				case "problem":
					identity.DeviceStatus, identity.ProblemCode = 0x400, 22
				case "phantom":
					identity.Phantom = true
				case "renamed-alias":
					row.Alias = "user-renamed-interface"
				}
				inspections, calls := 0, 0
				inspectExclusiveWindowsAdapter = func(requested string) (windowsAdapterInspection, error) {
					inspections++
					return inspectWindowsAdapterInterfaces(requested, []windowsInterfaceIdentity{row}, func(actualName string, guid windows.GUID) (windowsAdapterIdentity, error) {
						if actualName != name || guid != identity.GUID {
							t.Fatalf("wrong native identity query: name=%q guid=%s", actualName, guid)
						}
						return identity, nil
					})
				}
				createExclusiveWindowsAdapter = func(options Options) (windowsAdapter, error) {
					calls++
					if options.Name != name || inspections != 1 {
						t.Fatal("native creation must follow one complete product inspection")
					}
					return nil, windows.ERROR_ALREADY_EXISTS
				}
				device, err := newWindowsExclusive(Options{Name: name}, time.Now, func(time.Duration) {
					t.Fatal("verified product presence must not cause an alias grace wait")
				})
				if device != nil || !errors.Is(err, windows.ERROR_ALREADY_EXISTS) || calls != 1 {
					t.Fatalf("native ownership result was not preserved: device=%v err=%v calls=%d", device, err, calls)
				}
				for _, detail := range []string{"stage=create reason=native_busy", identity.GUID.String(), "native_code=0x000000b7", "actual_guid=", "luid=123", "index=18", "instance="} {
					if !strings.Contains(err.Error(), detail) {
						t.Fatalf("native failure lost diagnostic %q: %v", detail, err)
					}
				}
			})
		}
	}
}

func TestDesktopExclusiveAdapterDiagnosticHeaderRetainsNativeCodes(t *testing.T) {
	identity := desktopOwnedIdentity("MihomoDesktop")
	inspection := windowsAdapterInspection{
		ExpectedGUID: identity.GUID,
		Native:       &identity,
		Interfaces:   []windowsInterfaceIdentity{{GUID: identity.GUID, LUID: 123, Index: 18}},
	}
	raw := syscall.Errno(0x800700b7)
	err := inspection.failure("create", "native_busy", errors.Join(raw, windows.ERROR_ALREADY_EXISTS))
	header, _, found := strings.Cut(err.Error(), "]")
	if !found || !errors.Is(err, raw) || !errors.Is(err, windows.ERROR_ALREADY_EXISTS) {
		t.Fatalf("diagnostic wrapping lost native causes: %v", err)
	}
	for _, detail := range []string{"native_code=0x800700b7", "win32_code=183", "actual_guid=" + identity.GUID.String(), "luid=123", "index=18"} {
		if !strings.Contains(header, detail) {
			t.Fatalf("safe diagnostic header lost %q: %s", detail, header)
		}
	}
}

func TestDesktopExclusiveAdapterProtectsForeignAndUnknownInterfaces(t *testing.T) {
	name := "utun"
	owned := desktopOwnedIdentity(name)
	row := windowsInterfaceIdentity{Alias: name, GUID: owned.GUID, LUID: 123, Index: 18}
	legacy := row
	legacy.GUID = *generateGUIDByDeviceName(name)
	legacy.OperStatus = 2
	queryFailure := errors.New("SetupAPI access denied")
	for _, test := range []struct {
		name        string
		rows        []windowsInterfaceIdentity
		identity    windowsAdapterIdentity
		queryErr    error
		reason      string
		wantQueries int
		conflict    bool
	}{
		{name: "legacy down utun", rows: []windowsInterfaceIdentity{legacy}, reason: "foreign_alias", conflict: true},
		{name: "case insensitive foreign name", rows: []windowsInterfaceIdentity{{Alias: "UTUN", GUID: legacy.GUID}}, reason: "foreign_alias", conflict: true},
		{name: "ambiguous alias", rows: []windowsInterfaceIdentity{row, row}, reason: "ambiguous_alias"},
		{name: "IPHelper only row", rows: []windowsInterfaceIdentity{row}, queryErr: os.ErrNotExist, reason: "native_identity_missing", wantQueries: 1},
		{name: "native query failure", rows: []windowsInterfaceIdentity{row}, queryErr: queryFailure, reason: "native_query_failed", wantQueries: 1},
		{name: "missing native property is not absence", queryErr: errors.Join(errors.New("missing NetCfgInstanceId"), windows.ERROR_FILE_NOT_FOUND), reason: "native_query_failed", wantQueries: 1},
		{name: "unmarked native device", rows: []windowsInterfaceIdentity{row}, identity: windowsAdapterIdentity{GUID: owned.GUID, InstanceID: "unmarked"}, reason: "identity_mismatch", wantQueries: 1},
	} {
		t.Run(test.name, func(t *testing.T) {
			queries := 0
			inspection, err := inspectWindowsAdapterInterfaces(name, test.rows, func(string, windows.GUID) (windowsAdapterIdentity, error) {
				queries++
				return test.identity, test.queryErr
			})
			if err == nil || inspection.Reason != test.reason || queries != test.wantQueries {
				t.Fatalf("unsafe interface admitted or misclassified: reason=%q err=%v queries=%d", inspection.Reason, err, queries)
			}
			err = inspection.failure("inspect", inspection.Reason, err)
			if errors.Is(err, os.ErrExist) != test.conflict {
				t.Fatalf("uncertainty must not be reported as a proven name conflict: %v", err)
			}
			if test.queryErr == queryFailure && !errors.Is(err, queryFailure) {
				t.Fatalf("native query cause was lost: %v", err)
			}
			if !strings.Contains(err.Error(), "reason="+test.reason) || strings.Contains(err.Error(), "active, foreign") {
				t.Fatalf("failure must identify the actual branch without claiming an owner: %v", err)
			}
		})
	}
}

func TestDesktopExclusiveAdapterRequiresAllProductEvidence(t *testing.T) {
	name := "MihomoOwnershipTest"
	guid := *generateExclusiveGUIDByDeviceName(name)
	if guid != *generateExclusiveGUIDByDeviceName(name) || guid == *generateGUIDByDeviceName(name) || guid == *generateExclusiveGUIDByDeviceName(name + "other") {
		t.Fatal("product identity must be stable and separate from generic sing-tun")
	}
	for _, test := range []struct {
		name   string
		change func(*windowsAdapterIdentity)
	}{
		{"different actual GUID", func(i *windowsAdapterIdentity) { i.GUID = *generateGUIDByDeviceName(name) }},
		{"missing native instance", func(i *windowsAdapterIdentity) { i.InstanceID = "" }},
		{"different Wintun name", func(i *windowsAdapterIdentity) { i.Name += "-other" }},
		{"legacy generic product", func(i *windowsAdapterIdentity) { i.TunnelType = "Meta" }},
		{"changed native marker", func(i *windowsAdapterIdentity) { i.FriendlyName = "Foreign Tunnel" }},
		{"different hardware", func(i *windowsAdapterIdentity) { i.HardwareIDs = []string{"WireGuard"} }},
		{"ambiguous hardware", func(i *windowsAdapterIdentity) { i.HardwareIDs = []string{"Wintun", "other"} }},
		{"different device class", func(i *windowsAdapterIdentity) { i.ClassGUID = windows.GUID{} }},
	} {
		t.Run(test.name, func(t *testing.T) {
			identity := desktopOwnedIdentity(name)
			test.change(&identity)
			inspection, err := inspectWindowsAdapterInterfaces(name, nil, func(string, windows.GUID) (windowsAdapterIdentity, error) {
				return identity, nil
			})
			if err == nil || inspection.Reason != "identity_mismatch" || errors.Is(err, os.ErrExist) {
				t.Fatalf("unverified product identity admitted or claimed occupied: inspection=%+v err=%v", inspection, err)
			}
		})
	}
}

func TestDesktopExclusiveAdapterWaitsOnlyForUnverifiedAliasRemoval(t *testing.T) {
	originalCreate, originalInspect := createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter
	t.Cleanup(func() {
		createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter = originalCreate, originalInspect
	})
	for _, disappears := range []bool{true, false} {
		t.Run(map[bool]string{true: "removed", false: "still present"}[disappears], func(t *testing.T) {
			started := time.Unix(1, 0)
			current, inspections, calls := started, 0, 0
			createFailure := errors.New("native creation reached")
			inspectExclusiveWindowsAdapter = func(name string) (windowsAdapterInspection, error) {
				inspections++
				rows := []windowsInterfaceIdentity{{Alias: name, GUID: *generateGUIDByDeviceName(name)}}
				if disappears && inspections > 1 {
					rows = nil
				}
				return inspectWindowsAdapterInterfaces(name, rows, func(string, windows.GUID) (windowsAdapterIdentity, error) {
					return windowsAdapterIdentity{}, os.ErrNotExist
				})
			}
			createExclusiveWindowsAdapter = func(Options) (windowsAdapter, error) { calls++; return nil, createFailure }
			device, err := newWindowsExclusive(Options{Name: "owned-adapter"}, func() time.Time { return current }, func(delay time.Duration) {
				if delay <= 0 || delay > 50*time.Millisecond || current.Sub(started)+delay > 500*time.Millisecond {
					t.Fatalf("unverified removal grace exceeded its bound: elapsed=%s delay=%s", current.Sub(started), delay)
				}
				current = current.Add(delay)
			})
			if device != nil {
				t.Fatal("test creation must not return a native device")
			}
			if disappears {
				if !errors.Is(err, createFailure) || calls != 1 || current.Sub(started) != 50*time.Millisecond {
					t.Fatalf("removed alias still blocked creation: err=%v calls=%d waited=%s", err, calls, current.Sub(started))
				}
			} else if !errors.Is(err, os.ErrExist) || calls != 0 || current.Sub(started) != 500*time.Millisecond {
				t.Fatalf("foreign alias was mutated or retried without a bound: err=%v calls=%d waited=%s", err, calls, current.Sub(started))
			}
		})
	}
}

func TestDesktopExclusiveAdapterNeverOpensExisting(t *testing.T) {
	originalCreate, originalInspect := createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter
	t.Cleanup(func() {
		createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter = originalCreate, originalInspect
	})
	inspectExclusiveWindowsAdapter = func(name string) (windowsAdapterInspection, error) {
		return inspectWindowsAdapterInterfaces(name, nil, func(string, windows.GUID) (windowsAdapterIdentity, error) {
			return windowsAdapterIdentity{}, os.ErrNotExist
		})
	}
	for _, failure := range []error{os.ErrExist, os.ErrPermission, errors.New("driver unavailable")} {
		attempts := 0
		createExclusiveWindowsAdapter = func(Options) (windowsAdapter, error) { attempts++; return nil, failure }
		device, err := NewExclusive(Options{Name: "owned-adapter"})
		if device != nil || !errors.Is(err, failure) || attempts != 1 {
			t.Fatalf("failed Create must return without Open or overlapping retries: device=%v err=%v attempts=%d", device, err, attempts)
		}
	}
}

func TestDesktopExclusiveAdapterInspectionFailureNeverCreates(t *testing.T) {
	originalCreate, originalInspect := createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter
	t.Cleanup(func() {
		createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter = originalCreate, originalInspect
	})
	failure := windows.ERROR_ACCESS_DENIED
	for _, reason := range []string{"interface_query_failed", "native_query_failed"} {
		t.Run(reason, func(t *testing.T) {
			inspectExclusiveWindowsAdapter = func(name string) (windowsAdapterInspection, error) {
				return windowsAdapterInspection{ExpectedGUID: *generateExclusiveGUIDByDeviceName(name), Reason: reason}, failure
			}
			createExclusiveWindowsAdapter = func(Options) (windowsAdapter, error) {
				t.Fatal("unverified native state must not authorize creation")
				return nil, nil
			}
			device, err := newWindowsExclusive(Options{Name: "owned-adapter"}, time.Now, func(time.Duration) {
				t.Fatal("an API query failure must not be retried as alias removal")
			})
			if device != nil || !errors.Is(err, failure) || errors.Is(err, os.ErrExist) || !strings.Contains(err.Error(), "stage=inspect reason="+reason) {
				t.Fatalf("native inspection failure was misclassified: device=%v err=%v", device, err)
			}
		})
	}
}

func TestDesktopFailedSessionClosesOwnedAdapterBeforeRetry(t *testing.T) {
	originalCreate, originalInspect := createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter
	t.Cleanup(func() {
		createExclusiveWindowsAdapter, inspectExclusiveWindowsAdapter = originalCreate, originalInspect
	})
	inspectExclusiveWindowsAdapter = func(name string) (windowsAdapterInspection, error) {
		return windowsAdapterInspection{ExpectedGUID: *generateExclusiveGUIDByDeviceName(name), Reason: "adapter_absent"}, nil
	}
	failure := errors.New("driver session failure")
	previous := &failingOwnedAdapter{err: failure}
	createExclusiveWindowsAdapter = func(Options) (windowsAdapter, error) { return previous, nil }
	if device, err := NewExclusive(Options{Name: "owned-adapter"}); device != nil || !errors.Is(err, failure) || previous.closed != 1 || previous.readAfterClose != 0 {
		t.Fatalf("session failure lost cleanup or used a closed handle: device=%v err=%v closes=%d reads_after_close=%d", device, err, previous.closed, previous.readAfterClose)
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
