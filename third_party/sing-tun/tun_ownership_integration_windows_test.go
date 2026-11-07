package tun

import (
	"context"
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/netip"
	"os"
	"os/exec"
	"path/filepath"
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
	requireDesktopNativeIntegration(t)
	if runDesktopNativeHarness(t) {
		return
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
	if fixtureGUID == *generateExclusiveGUIDByDeviceName(name) {
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
	if err != nil || *actualGUID == *generateExclusiveGUIDByDeviceName(name) {
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
	options := desktopNativeFixtureOptions(name)
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

func requireDesktopNativeIntegration(t *testing.T) {
	t.Helper()
	if os.Getenv("MIHOMO_TUN_INTEGRATION") != "1" {
		t.Skip("native Wintun lifecycle test requires MIHOMO_TUN_INTEGRATION=1")
	}
	if deadline, ok := t.Deadline(); !ok || time.Until(deadline) > 2*time.Minute {
		t.Fatal("native Wintun lifecycle test requires go test -timeout 2m or shorter")
	}
	if !windows.GetCurrentProcessToken().IsElevated() {
		t.Fatal("native Wintun lifecycle test requires an elevated Windows test runner")
	}
}

// Each native scenario runs in a fresh process. Wintun queues global orphan
// cleanup after Create/Close, so an earlier lifecycle scenario must have exited
// before the crash test can demonstrate that its own preflight recovers residue.
// The outer test process never loads Wintun through either native scenario.
func runDesktopNativeHarness(t *testing.T) bool {
	t.Helper()
	if os.Getenv("MIHOMO_TUN_NATIVE_HARNESS") == t.Name() {
		return false
	}
	deadline, _ := t.Deadline()
	timeout := 90 * time.Second
	if remaining := time.Until(deadline) - 5*time.Second; remaining < timeout {
		timeout = remaining
	}
	if timeout <= 0 {
		t.Fatal("insufficient time for native Wintun harness")
	}
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	command := exec.CommandContext(ctx, os.Args[0], "-test.run=^"+t.Name()+"$", "-test.v", "-test.timeout="+timeout.String())
	command.Env = append(os.Environ(), "MIHOMO_TUN_NATIVE_HARNESS="+t.Name())
	command.Stdout = os.Stdout
	command.Stderr = os.Stderr
	if err := command.Run(); err != nil {
		t.Fatalf("native Wintun harness failed: %v (context: %v)", err, ctx.Err())
	}
	return true
}

func desktopNativeFixtureOptions(name string) Options {
	return Options{
		Name:         name,
		MTU:          1500,
		Inet4Address: []netip.Prefix{netip.MustParsePrefix("198.18.255.1/30")},
		AutoRoute:    false,
		DNSMode:      DNSModeDisabled,
	}
}

type desktopNativeCrashReady struct {
	PID        int          `json:"pid"`
	LUID       uint64       `json:"luid"`
	GUID       windows.GUID `json:"guid"`
	InstanceID string       `json:"instanceId"`
}

func TestDesktopNativeExclusiveAdapterCrashRecovery(t *testing.T) {
	requireDesktopNativeIntegration(t)
	if runDesktopNativeHarness(t) {
		return
	}
	createAdapter := createExclusiveWindowsAdapter
	createAttempts := 0
	createExclusiveWindowsAdapter = func(options Options) (windowsAdapter, error) {
		createAttempts++
		return createAdapter(options)
	}
	t.Cleanup(func() { createExclusiveWindowsAdapter = createAdapter })

	var nonce [12]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		t.Fatal(err)
	}
	name := fmt.Sprintf("MihomoCrash-%x", nonce)
	expectedGUID := *generateExclusiveGUIDByDeviceName(name)
	readyPath := filepath.Join(t.TempDir(), "ready.json")
	deadline, _ := t.Deadline()
	ownerDeadline := time.Now().Add(45 * time.Second)
	if limit := deadline.Add(-5 * time.Second); limit.Before(ownerDeadline) {
		ownerDeadline = limit
	}
	ctx, cancel := context.WithDeadline(context.Background(), ownerDeadline)
	t.Cleanup(cancel)
	command := exec.CommandContext(ctx, os.Args[0], "-test.run=^TestDesktopNativeExclusiveAdapterCrashOwner$", "-test.timeout=50s")
	command.Env = append(os.Environ(), "MIHOMO_TUN_CRASH_ALIAS="+name, "MIHOMO_TUN_CRASH_READY="+readyPath)
	command.Stdout = os.Stdout
	command.Stderr = os.Stderr
	input, err := command.StdinPipe()
	if err != nil {
		t.Fatal(err)
	}
	if err := command.Start(); err != nil {
		_ = input.Close()
		t.Fatal(err)
	}
	exited := make(chan error, 1)
	go func() { exited <- command.Wait() }()
	ownerExited := false
	t.Cleanup(func() {
		// Before the deliberate crash, failures can ask the exact fixture child
		// to close normally. Never open or delete an adapter from this cleanup.
		_ = input.Close()
		if ownerExited {
			return
		}
		select {
		case <-exited:
			return
		case <-time.After(2 * time.Second):
		}
		_ = command.Process.Kill()
		select {
		case <-exited:
		case <-time.After(5 * time.Second):
			t.Error("owned fixture process did not exit after cleanup termination")
		}
	})

	var ready desktopNativeCrashReady
	readyTimeout := time.NewTimer(30 * time.Second)
	defer readyTimeout.Stop()
	poll := time.NewTicker(25 * time.Millisecond)
	defer poll.Stop()
readyLoop:
	for {
		select {
		case err := <-exited:
			ownerExited = true
			t.Fatalf("fixture process exited before readiness: %v", err)
		case <-ctx.Done():
			t.Fatalf("fixture process readiness cancelled: %v", ctx.Err())
		case <-readyTimeout.C:
			t.Fatal("fixture process did not create its native adapter within 30s")
		case <-poll.C:
			data, err := os.ReadFile(readyPath)
			if errors.Is(err, os.ErrNotExist) {
				continue
			}
			if err != nil {
				t.Fatal(err)
			}
			if err := json.Unmarshal(data, &ready); err != nil {
				t.Fatalf("decode fixture identity: %v", err)
			}
			break readyLoop
		}
	}
	if ready.PID != command.Process.Pid || ready.LUID == 0 || ready.GUID != expectedGUID || ready.InstanceID == "" {
		t.Fatalf("invalid native fixture readiness: %+v; expected pid=%d guid=%s", ready, command.Process.Pid, expectedGUID.String())
	}
	assertHeldIdentity := func() {
		t.Helper()
		row, err := winipcfg.LUID(ready.LUID).Interface()
		if err != nil || row.InterfaceLUID != winipcfg.LUID(ready.LUID) || row.InterfaceGUID != ready.GUID || row.Alias() != name {
			t.Fatalf("live fixture alias/LUID/GUID changed: row=%+v err=%v; ready=%+v", row, err, ready)
		}
		identity, err := queryWindowsAdapter(name, expectedGUID)
		if err != nil || identity.InstanceID != ready.InstanceID || identity.GUID != expectedGUID || identity.Name != name || identity.TunnelType != exclusiveWindowsTunnelType || identity.Phantom {
			t.Fatalf("live fixture native identity changed: identity=%+v err=%v; ready=%+v", identity, err, ready)
		}
	}
	assertHeldIdentity()
	t.Logf("owned child ready: pid=%d luid=%d guid=%s instance=%q", ready.PID, ready.LUID, ready.GUID.String(), ready.InstanceID)
	options := desktopNativeFixtureOptions(name)
	conflicting, conflictErr := NewExclusive(options)
	if conflicting != nil {
		// Close only a handle returned by this constructor; never reopen the
		// first child's adapter to clean up a failed assertion.
		t.Cleanup(func() { _ = conflicting.Close() })
	}
	if conflicting != nil || !errors.Is(conflictErr, os.ErrExist) || createAttempts != 0 {
		t.Fatalf("live product adapter must be rejected before native creation: adapter=%v err=%v creates=%d", conflicting, conflictErr, createAttempts)
	}
	assertHeldIdentity()
	select {
	case err := <-exited:
		ownerExited = true
		t.Fatalf("fixture process exited before forced termination: %v", err)
	default:
	}

	// On Windows Kill uses TerminateProcess. Wait for this exact child before
	// attempting recovery; its deferred adapter.Close must not run.
	if err := command.Process.Kill(); err != nil {
		t.Fatalf("force termination of fixture process: %v", err)
	}
	select {
	case err := <-exited:
		ownerExited = true
		var exitError *exec.ExitError
		if !errors.As(err, &exitError) || command.ProcessState.ExitCode() == 0 {
			t.Fatalf("fixture did not report a forced nonzero exit: %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("forced fixture process exit was not confirmed")
	}
	started := time.Now()
	residue, residueErr := queryWindowsAdapter(name, expectedGUID)
	t.Logf("immediately after forced exit: identity=%+v err=%v", residue, residueErr)
	// No sleep, native Open, dummy creation, or extra recovery attempt: this
	// fresh harness has never called Wintun and cannot have queued cleanup.
	owned, err := NewExclusive(options)
	if err != nil {
		t.Fatalf("single production recreate after forced exit failed after %s: %v", time.Since(started), err)
	}
	t.Cleanup(func() { _ = owned.Close() })
	if createAttempts != 1 {
		t.Fatalf("crash recovery must invoke native creation exactly once, got %d", createAttempts)
	}
	native, ok := owned.(*NativeTun)
	if !ok || native.InterfaceLUID() == 0 {
		t.Fatal("forced-exit recovery did not return an owned native adapter")
	}
	row, err := winipcfg.LUID(native.InterfaceLUID()).Interface()
	if err != nil || row.InterfaceLUID != winipcfg.LUID(native.InterfaceLUID()) || row.InterfaceGUID != expectedGUID || row.Alias() != name {
		t.Fatalf("recreated adapter has wrong native identity: row=%+v err=%v", row, err)
	}
	recovered, err := queryWindowsAdapter(name, expectedGUID)
	if err != nil || recovered.InstanceID != ready.InstanceID || recovered.GUID != expectedGUID || recovered.Name != name || recovered.TunnelType != exclusiveWindowsTunnelType || recovered.Phantom {
		t.Fatalf("recreated adapter did not regain the exact product instance: identity=%+v err=%v", recovered, err)
	}
	assertDesktopNativeSession(t, native)
	t.Logf("forced-exit recreate completed in %s: previousLUID=%d currentLUID=%d guid=%s instance=%q", time.Since(started), ready.LUID, native.InterfaceLUID(), expectedGUID.String(), recovered.InstanceID)
}

func TestDesktopNativeExclusiveAdapterCrashOwner(t *testing.T) {
	name := os.Getenv("MIHOMO_TUN_CRASH_ALIAS")
	readyPath := os.Getenv("MIHOMO_TUN_CRASH_READY")
	if name == "" || readyPath == "" {
		t.Skip("native crash fixture runs only as an explicitly requested subprocess")
	}
	requireDesktopNativeIntegration(t)
	if !strings.HasPrefix(name, "MihomoCrash-") {
		t.Fatal("invalid native crash fixture alias")
	}
	aliases, err := listWindowsInterfaceAliases()
	if err != nil {
		t.Fatal(err)
	}
	for _, alias := range aliases {
		if strings.EqualFold(alias, name) {
			t.Fatal("random fixture alias already exists; refusing to create it")
		}
	}
	owned, err := NewExclusive(desktopNativeFixtureOptions(name))
	if err != nil {
		t.Fatalf("create subprocess fixture adapter: %v", err)
	}
	defer owned.Close()
	native, ok := owned.(*NativeTun)
	if !ok || native.InterfaceLUID() == 0 {
		t.Fatal("subprocess fixture has no native adapter")
	}
	guid, err := winipcfg.LUID(native.InterfaceLUID()).GUID()
	if err != nil || *guid != *generateExclusiveGUIDByDeviceName(name) {
		t.Fatalf("subprocess fixture has wrong GUID: guid=%v err=%v", guid, err)
	}
	identity, err := queryWindowsAdapter(name, *guid)
	if err != nil || identity.GUID != *guid || identity.Name != name || identity.TunnelType != exclusiveWindowsTunnelType || identity.InstanceID == "" || identity.Phantom {
		t.Fatalf("subprocess fixture native identity is invalid: identity=%+v err=%v", identity, err)
	}
	assertDesktopNativeSession(t, native)
	data, err := json.Marshal(desktopNativeCrashReady{PID: os.Getpid(), LUID: native.InterfaceLUID(), GUID: *guid, InstanceID: identity.InstanceID})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(readyPath+".tmp", data, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.Rename(readyPath+".tmp", readyPath); err != nil {
		t.Fatal(err)
	}
	// Keep the actual adapter and session alive until the parent terminates us.
	// EOF permits normal cleanup only when the parent aborts before that crash.
	var input [1]byte
	if _, err := io.ReadFull(os.Stdin, input[:]); err != nil && !errors.Is(err, io.EOF) {
		t.Fatalf("wait for fixture parent: %v", err)
	}
}

func assertDesktopNativeSession(t *testing.T, native *NativeTun) {
	t.Helper()
	event := native.session.ReadWaitEvent()
	if event == 0 || event != native.readWait {
		t.Fatal("native TUN session has no matching read event")
	}
	state, err := windows.WaitForSingleObject(event, 0)
	if err != nil || (state != windows.WAIT_OBJECT_0 && state != uint32(windows.WAIT_TIMEOUT)) {
		t.Fatalf("native TUN read event is not usable: state=%d err=%v", state, err)
	}
	packet, err := native.session.ReceivePacket()
	if err == nil {
		if len(packet) == 0 {
			t.Fatal("native TUN session returned an empty packet")
		}
		native.session.ReleaseReceivePacket(packet)
	} else if !errors.Is(err, windows.ERROR_NO_MORE_ITEMS) {
		t.Fatalf("native TUN session receive failed: %v", err)
	}
}
