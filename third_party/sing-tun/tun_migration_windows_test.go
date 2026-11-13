package tun

import (
	"errors"
	"fmt"
	"os"
	"strings"
	"testing"

	"github.com/metacubex/sing-tun/internal/winipcfg"
	"github.com/metacubex/sing-tun/internal/wintun"
	"golang.org/x/sys/windows"
)

type migrationTestAdapter struct {
	luid       uint64
	closed     int
	sessions   int
	closeErr   error
	sessionErr error
}

func (a *migrationTestAdapter) LUID() uint64 { return a.luid }
func (a *migrationTestAdapter) Close() error {
	a.closed++
	return a.closeErr
}
func (a *migrationTestAdapter) StartSession(uint32) (wintun.Session, error) {
	a.sessions++
	return wintun.Session{}, a.sessionErr
}

func legacyProductRow(name, alias string) windowsInterfaceIdentity {
	return windowsInterfaceIdentity{
		Alias: alias, GUID: *generateExclusiveGUIDByDeviceName(name),
		LUID: 123, Index: 18, Type: uint32(winipcfg.IfTypePropVirtual),
	}
}

func TestStandaloneWindowsMigrationLeavesOtherNamespacesToUpstream(t *testing.T) {
	name := "utun"
	for _, test := range []struct {
		name string
		rows []windowsInterfaceIdentity
	}{
		{"empty", nil},
		{"generic same alias", []windowsInterfaceIdentity{{Alias: name, GUID: *generateGUIDByDeviceName(name)}}},
		{"foreign same alias", []windowsInterfaceIdentity{{Alias: name, GUID: windows.GUID{Data1: 42}}}},
		{"other product name", []windowsInterfaceIdentity{legacyProductRow("other-product", "other-product")}},
	} {
		t.Run(test.name, func(t *testing.T) {
			reads := 0
			err := migrateWindowsProductAdapter(name, func() ([]windowsInterfaceIdentity, error) {
				reads++
				return test.rows, nil
			}, func(string, windows.GUID) (windowsAdapterIdentity, error) {
				t.Fatal("migration must not inspect another namespace's native properties")
				return windowsAdapterIdentity{}, nil
			}, func(string, windows.GUID) (windowsAdapter, error) {
				t.Fatal("migration must not claim another namespace")
				return nil, nil
			})
			if err != nil || reads != 1 {
				t.Fatalf("upstream path was blocked: reads=%d err=%v", reads, err)
			}
		})
	}
}

func TestStandaloneWindowsMigrationClaimsOriginalGUIDUnderVerifiedCurrentAlias(t *testing.T) {
	name := "utun"
	for _, alias := range []string{name, "utun 1", "UTUN 2", "renamed-product"} {
		t.Run(alias, func(t *testing.T) {
			row := legacyProductRow(name, alias)
			// An up link cannot stand in for native creator arbitration either.
			row.OperStatus, row.AdminState = 1, 1
			rows := []windowsInterfaceIdentity{row}
			if alias != name {
				// The old original alias can already belong to a generic adapter.
				// Only the verified product's current alias may be used for claim.
				rows = append(rows, windowsInterfaceIdentity{Alias: name, GUID: *generateGUIDByDeviceName(name)})
			}
			identity := desktopOwnedIdentity(name)
			identity.Name = alias
			adapter := &migrationTestAdapter{luid: 456}
			queries, claims := 0, 0
			err := migrateWindowsProductAdapter(name, func() ([]windowsInterfaceIdentity, error) {
				return rows, nil
			}, func(actualAlias string, guid windows.GUID) (windowsAdapterIdentity, error) {
				queries++
				if actualAlias != alias || guid != row.GUID {
					t.Fatalf("query lost original GUID/current alias: %q %s", actualAlias, guid)
				}
				return identity, nil
			}, func(actualAlias string, guid windows.GUID) (windowsAdapter, error) {
				claims++
				if queries != 1 || actualAlias != alias || guid != row.GUID {
					t.Fatalf("claim lost verified identity: queries=%d alias=%q guid=%s", queries, actualAlias, guid)
				}
				return adapter, nil
			})
			if err != nil || queries != 1 || claims != 1 || adapter.closed != 1 || adapter.sessions != 0 {
				t.Fatalf("claim must close exactly its own handle without a session: queries=%d claims=%d adapter=%+v err=%v", queries, claims, adapter, err)
			}
		})
	}
}

func TestStandaloneWindowsMigrationRetiringRowKeepsExistingAbsenceBoundary(t *testing.T) {
	name := "utun"
	for _, test := range []struct {
		name   string
		change func(*windowsInterfaceIdentity)
		err    error
		admit  bool
	}{
		{"original row", func(*windowsInterfaceIdentity) {}, os.ErrNotExist, true},
		{"case folded original", func(row *windowsInterfaceIdentity) { row.Alias = "UTUN" }, os.ErrNotExist, true},
		{"description is not ownership", func(row *windowsInterfaceIdentity) { row.Description = "driver description #2" }, os.ErrNotExist, true},
		{"renamed row", func(row *windowsInterfaceIdentity) { row.Alias += " 1" }, os.ErrNotExist, false},
		{"zero luid", func(row *windowsInterfaceIdentity) { row.LUID = 0 }, os.ErrNotExist, false},
		{"zero index", func(row *windowsInterfaceIdentity) { row.Index = 0 }, os.ErrNotExist, false},
		{"wrong type", func(row *windowsInterfaceIdentity) { row.Type = uint32(winipcfg.IfTypeEthernetCSMACD) }, os.ErrNotExist, false},
		{"missing property", func(*windowsInterfaceIdentity) {}, windows.ERROR_FILE_NOT_FOUND, false},
		{"wrapped missing property", func(*windowsInterfaceIdentity) {}, fmt.Errorf("candidate property: %w", os.ErrNotExist), false},
		{"permission error", func(*windowsInterfaceIdentity) {}, windows.ERROR_ACCESS_DENIED, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			row := legacyProductRow(name, name)
			test.change(&row)
			adapter := &migrationTestAdapter{luid: 456}
			claims := 0
			err := migrateWindowsProductAdapter(name, func() ([]windowsInterfaceIdentity, error) {
				return []windowsInterfaceIdentity{row}, nil
			}, func(string, windows.GUID) (windowsAdapterIdentity, error) {
				return windowsAdapterIdentity{}, test.err
			}, func(string, windows.GUID) (windowsAdapter, error) {
				claims++
				return adapter, nil
			})
			if test.admit {
				if err != nil || claims != 1 || adapter.closed != 1 || adapter.sessions != 0 {
					t.Fatalf("valid retiring row did not reach native claim: claims=%d adapter=%+v err=%v", claims, adapter, err)
				}
			} else if err == nil || claims != 0 || adapter.closed != 0 {
				t.Fatalf("unverified retiring row authorized a claim: claims=%d adapter=%+v err=%v", claims, adapter, err)
			}
		})
	}
}

func TestStandaloneWindowsMigrationRejectsIncompleteOrConflictingIdentity(t *testing.T) {
	name := "utun"
	for _, test := range []struct {
		name   string
		change func(*[]windowsInterfaceIdentity, *windowsAdapterIdentity)
	}{
		{"duplicate product GUID", func(rows *[]windowsInterfaceIdentity, _ *windowsAdapterIdentity) { *rows = append(*rows, (*rows)[0]) }},
		{"alias shared with foreign GUID", func(rows *[]windowsInterfaceIdentity, _ *windowsAdapterIdentity) {
			*rows = append(*rows, windowsInterfaceIdentity{Alias: "UTUN", GUID: *generateGUIDByDeviceName(name)})
		}},
		{"empty alias", func(rows *[]windowsInterfaceIdentity, _ *windowsAdapterIdentity) { (*rows)[0].Alias = "" }},
		{"native name mismatch", func(_ *[]windowsInterfaceIdentity, identity *windowsAdapterIdentity) { identity.Name += " other" }},
		{"native GUID mismatch", func(_ *[]windowsInterfaceIdentity, identity *windowsAdapterIdentity) {
			identity.GUID = *generateGUIDByDeviceName(name)
		}},
		{"native instance missing", func(_ *[]windowsInterfaceIdentity, identity *windowsAdapterIdentity) { identity.InstanceID = "" }},
		{"generic tunnel type", func(_ *[]windowsInterfaceIdentity, identity *windowsAdapterIdentity) { identity.TunnelType = "Meta" }},
		{"generic friendly name", func(_ *[]windowsInterfaceIdentity, identity *windowsAdapterIdentity) {
			identity.FriendlyName = "Meta Tunnel"
		}},
		{"wrong class", func(_ *[]windowsInterfaceIdentity, identity *windowsAdapterIdentity) {
			identity.ClassGUID = windows.GUID{}
		}},
		{"wrong hardware", func(_ *[]windowsInterfaceIdentity, identity *windowsAdapterIdentity) {
			identity.HardwareIDs = []string{"other"}
		}},
		{"extra hardware", func(_ *[]windowsInterfaceIdentity, identity *windowsAdapterIdentity) {
			identity.HardwareIDs = []string{"Wintun", "other"}
		}},
	} {
		t.Run(test.name, func(t *testing.T) {
			rows := []windowsInterfaceIdentity{legacyProductRow(name, name)}
			identity := desktopOwnedIdentity(name)
			test.change(&rows, &identity)
			err := migrateWindowsProductAdapter(name, func() ([]windowsInterfaceIdentity, error) {
				return rows, nil
			}, func(string, windows.GUID) (windowsAdapterIdentity, error) {
				return identity, nil
			}, func(string, windows.GUID) (windowsAdapter, error) {
				t.Fatal("unverified or conflicting identity must not reach native claim")
				return nil, nil
			})
			if err == nil {
				t.Fatal("unverified legacy product was admitted")
			}
		})
	}
}

func TestStandaloneWindowsMigrationQueryFailurePreservesCause(t *testing.T) {
	name := "utun"
	failure := windows.ERROR_ACCESS_DENIED
	for _, failInterfaces := range []bool{false, true} {
		err := migrateWindowsProductAdapter(name, func() ([]windowsInterfaceIdentity, error) {
			if failInterfaces {
				return nil, failure
			}
			return []windowsInterfaceIdentity{legacyProductRow(name, name)}, nil
		}, func(string, windows.GUID) (windowsAdapterIdentity, error) {
			return windowsAdapterIdentity{}, failure
		}, func(string, windows.GUID) (windowsAdapter, error) {
			t.Fatal("a query failure must not reach native claim")
			return nil, nil
		})
		if !errors.Is(err, failure) {
			t.Fatalf("query cause was lost (interfaces=%v): %v", failInterfaces, err)
		}
	}
}

func TestStandaloneWindowsMigrationFailureNeverEntersGenericFallback(t *testing.T) {
	originalMigrate, originalCreate := migrateLegacyWindowsAdapter, createWindowsAdapter
	t.Cleanup(func() { migrateLegacyWindowsAdapter, createWindowsAdapter = originalMigrate, originalCreate })
	name := "utun"
	createWindowsAdapter = func(Options) (windowsAdapter, error) {
		t.Fatal("failed product migration must return before generic Create/Open")
		return nil, nil
	}
	for _, failure := range []error{windows.ERROR_ALREADY_EXISTS, windows.ERROR_BUSY, windows.ERROR_ACCESS_DENIED} {
		for _, missingPnP := range []bool{false, true} {
			claims := 0
			migrateLegacyWindowsAdapter = func(requested string) error {
				return migrateWindowsProductAdapter(requested, func() ([]windowsInterfaceIdentity, error) {
					return []windowsInterfaceIdentity{legacyProductRow(name, name)}, nil
				}, func(string, windows.GUID) (windowsAdapterIdentity, error) {
					if missingPnP {
						return windowsAdapterIdentity{}, os.ErrNotExist
					}
					return desktopOwnedIdentity(name), nil
				}, func(string, windows.GUID) (windowsAdapter, error) {
					claims++
					// This includes a live software-device creator with no session:
					// no StartSession probe can establish its ownership for us.
					return nil, failure
				})
			}
			device, err := New(Options{Name: name})
			if device != nil || !errors.Is(err, failure) || claims != 1 ||
				!strings.Contains(err.Error(), "stage=migrate reason=legacy_product_claim_failed") {
				t.Fatalf("native creator result was not returned directly: missingPnP=%v device=%v claims=%d err=%v", missingPnP, device, claims, err)
			}
		}
	}
}

func TestStandaloneWindowsMigrationClosesClaimBeforeGenericCreate(t *testing.T) {
	originalMigrate, originalCreate := migrateLegacyWindowsAdapter, createWindowsAdapter
	t.Cleanup(func() { migrateLegacyWindowsAdapter, createWindowsAdapter = originalMigrate, originalCreate })
	name := "utun"
	legacy := &migrationTestAdapter{luid: 123}
	sessionFailure := errors.New("generic session fixture reached")
	generic := &migrationTestAdapter{luid: 456, sessionErr: sessionFailure}
	migrateLegacyWindowsAdapter = func(requested string) error {
		return migrateWindowsProductAdapter(requested, func() ([]windowsInterfaceIdentity, error) {
			return []windowsInterfaceIdentity{legacyProductRow(name, name)}, nil
		}, func(string, windows.GUID) (windowsAdapterIdentity, error) {
			return desktopOwnedIdentity(name), nil
		}, func(string, windows.GUID) (windowsAdapter, error) { return legacy, nil })
	}
	creates := 0
	createWindowsAdapter = func(options Options) (windowsAdapter, error) {
		creates++
		if legacy.closed != 1 || legacy.sessions != 0 || options.Name != name {
			t.Fatalf("generic Create preceded old creator close or changed the requested name: legacy=%+v options=%+v", legacy, options)
		}
		return generic, nil
	}
	device, err := New(Options{Name: name})
	if device != nil || !errors.Is(err, sessionFailure) || creates != 1 ||
		generic.sessions != 1 || generic.closed != 1 || legacy.closed != 1 {
		t.Fatalf("generic session path or deterministic cleanup changed: device=%v err=%v creates=%d legacy=%+v generic=%+v", device, err, creates, legacy, generic)
	}
	legacy = &migrationTestAdapter{luid: 123, closeErr: windows.ERROR_ACCESS_DENIED}
	if device, err = New(Options{Name: name}); device != nil || !errors.Is(err, legacy.closeErr) || creates != 1 ||
		legacy.closed != 1 || !strings.Contains(err.Error(), "legacy_product_close_failed") {
		t.Fatalf("failed creator close was ignored: device=%v err=%v creates=%d", device, err, creates)
	}
}

func TestStandaloneWindowsCreatedProductHandleMustMatchRequestedIdentity(t *testing.T) {
	expected := *generateExclusiveGUIDByDeviceName("utun")
	for _, test := range []struct {
		name string
		luid uint64
		guid windows.GUID
		err  error
		ok   bool
	}{
		{"same GUID", 123, expected, nil, true},
		{"other GUID", 123, *generateGUIDByDeviceName("utun"), nil, false},
		{"zero LUID", 0, expected, nil, false},
		{"GUID lookup error", 123, windows.GUID{}, windows.ERROR_NOT_FOUND, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			adapter := &migrationTestAdapter{luid: test.luid}
			reads := 0
			actual, err := verifyCreatedWindowsProductAdapter(adapter, expected, func(luid uint64) (windows.GUID, error) {
				reads++
				if luid != test.luid || adapter.closed != 0 {
					t.Fatal("identity must be read from the newly created handle before close")
				}
				return test.guid, test.err
			})
			if test.ok {
				if err != nil || actual != adapter || adapter.closed != 0 {
					t.Fatalf("valid created handle rejected: actual=%v adapter=%+v err=%v", actual, adapter, err)
				}
			} else if err == nil || actual != nil || adapter.closed != 1 || (test.err != nil && !errors.Is(err, test.err)) {
				t.Fatalf("unverified handle escaped or cleanup lost: actual=%v adapter=%+v err=%v", actual, adapter, err)
			}
			if reads != 1 || adapter.sessions != 0 {
				t.Fatalf("identity check opened a session or repeated lookup: reads=%d adapter=%+v", reads, adapter)
			}
		})
	}
}
