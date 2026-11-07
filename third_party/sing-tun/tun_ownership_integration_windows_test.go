package tun

import (
	"crypto/rand"
	"errors"
	"fmt"
	"net/netip"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/metacubex/sing-tun/internal/winipcfg"
	"github.com/metacubex/sing-tun/internal/wintun"
	"golang.org/x/sys/windows"
)

// Run only on an elevated Windows test runner with:
// MIHOMO_TUN_INTEGRATION=1 go test -timeout 2m -run '^TestDesktop' .
// The process timeout bounds synchronous native driver calls that cannot be
// cancelled safely from a test goroutine.
func TestDesktopNativeExclusiveAdapterLifecycle(t *testing.T) {
	if os.Getenv("MIHOMO_TUN_INTEGRATION") != "1" {
		t.Skip("native Wintun lifecycle test requires MIHOMO_TUN_INTEGRATION=1")
	}
	if deadline, ok := t.Deadline(); !ok || time.Until(deadline) > 2*time.Minute {
		t.Fatal("native Wintun lifecycle test requires go test -timeout 2m or shorter")
	}
	if !windows.GetCurrentProcessToken().IsElevated() {
		t.Fatal("native Wintun lifecycle test requires an elevated Windows test runner")
	}

	var nonce [12]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		t.Fatal(err)
	}
	name := fmt.Sprintf("MihomoAlias-%x", nonce)
	aliases, err := listWindowsInterfaceAliases()
	if err != nil {
		t.Fatal(err)
	}
	for _, alias := range aliases {
		if strings.EqualFold(alias, name) {
			t.Fatal("random fixture alias already exists; refusing to create it")
		}
	}
	fixtureGUID, err := windows.GenerateGUID()
	if err != nil {
		t.Fatal(err)
	}
	if fixtureGUID == *generateGUIDByDeviceName(name) {
		t.Fatal("fixture GUID must differ from the exclusive constructor's GUID")
	}

	fixture, err := wintun.CreateAdapter(name, TunnelType, &fixtureGUID)
	if err != nil {
		t.Fatalf("create independent fixture adapter: %v", err)
	}
	t.Cleanup(func() {
		if fixture != nil {
			// The underlying WintunCloseAdapter API returns VOID. Verify its
			// lifecycle through recreation, not the wrapper's return register.
			_ = fixture.Close()
		}
	})
	fixtureLUID := fixture.LUID()
	if fixtureLUID == 0 {
		t.Fatal("fixture adapter has no LUID")
	}
	actualGUID, err := winipcfg.LUID(fixtureLUID).GUID()
	if err != nil || *actualGUID == *generateGUIDByDeviceName(name) {
		t.Fatalf("fixture must have an independent actual GUID: guid=%v err=%v", actualGUID, err)
	}
	assertAlias := func(luid uint64) {
		t.Helper()
		row, err := winipcfg.LUID(luid).Interface()
		if err != nil {
			t.Fatalf("read held adapter LUID %d: %v", luid, err)
		}
		if row.InterfaceLUID != winipcfg.LUID(luid) || row.Alias() != name {
			t.Fatalf("held adapter identity changed: luid=%d alias=%q, want luid=%d alias=%q", row.InterfaceLUID, row.Alias(), luid, name)
		}
	}
	assertAlias(fixtureLUID)
	options := Options{
		Name:         name,
		MTU:          1500,
		Inet4Address: []netip.Prefix{netip.MustParsePrefix("198.18.255.1/30")},
		AutoRoute:    false,
		DNSMode:      DNSModeDisabled,
	}
	conflicting, conflictErr := NewExclusive(options)
	if conflicting != nil {
		t.Cleanup(func() { _ = conflicting.Close() })
	}
	if conflicting != nil || !errors.Is(conflictErr, os.ErrExist) {
		t.Errorf("same-alias creation must fail without adoption: adapter=%v err=%v", conflicting, conflictErr)
	}
	if after := fixture.LUID(); after != fixtureLUID {
		t.Fatalf("conflict changed the fixture LUID: before=%d after=%d", fixtureLUID, after)
	}
	assertAlias(fixtureLUID)
	if t.Failed() {
		t.FailNow()
	}

	var owned WinTun
	t.Cleanup(func() {
		if owned != nil {
			_ = owned.Close()
		}
	})
	for cycle := 1; cycle <= 2; cycle++ {
		if fixture != nil {
			_ = fixture.Close()
			fixture = nil
		} else {
			_ = owned.Close()
			owned = nil
		}
		// Do not poll or sleep here: the production 500 ms alias grace must
		// handle asynchronous removal immediately after the owned handle closes.
		started := time.Now()
		owned, err = NewExclusive(options)
		if err != nil {
			t.Fatalf("immediate recreate cycle %d after %s: %v", cycle, time.Since(started), err)
		}
		native, ok := owned.(*NativeTun)
		if !ok || native.InterfaceLUID() == 0 {
			t.Fatalf("recreate cycle %d did not return an owned native adapter", cycle)
		}
		assertAlias(native.InterfaceLUID())
		t.Logf("immediate recreate cycle %d completed in %s with LUID %d", cycle, time.Since(started), native.InterfaceLUID())
	}
}
