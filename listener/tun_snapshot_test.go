package listener

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"sync"
	"testing"
	"time"

	C "github.com/metacubex/mihomo/constant"
	LC "github.com/metacubex/mihomo/listener/config"
)

func waitTunSignal(t *testing.T, signal <-chan struct{}) {
	t.Helper()
	select {
	case <-signal:
	case <-time.After(2 * time.Second):
		t.Fatal("TUN test operation did not reach its bounded synchronization point")
	}
}

func TestTunConfigAndHealthSnapshotsDoNotWaitForNativeCloseOrCreate(t *testing.T) {
	live := setupTunHealth(t)
	InspectTunHealth()
	closeEntered, closeRelease := make(chan struct{}), make(chan struct{})
	createEntered, createRelease := make(chan struct{}), make(chan struct{})
	var closeOnce, createOnce sync.Once
	live.onClose = func() { close(closeEntered); <-closeRelease }
	newTunListener = func(cfg LC.Tun, _ C.Tunnel) (tunListener, error) {
		close(createEntered)
		<-createRelease
		return &healthTestTun{fakeTunListener: &fakeTunListener{config: cfg, device: "test-tun", index: 42}, owned: true, routes: true}, nil
	}
	wanted := LastTunConf.Clone()
	wanted.MTU = 1400
	done := make(chan struct{})
	var operationErr error
	go func() { operationErr = ReCreateTun(wanted, nil); close(done) }()
	t.Cleanup(func() {
		closeOnce.Do(func() { close(closeRelease) })
		createOnce.Do(func() { close(createRelease) })
		waitTunSignal(t, done)
	})
	assertResponsive := func(expectedMTU uint32) {
		t.Helper()
		read := make(chan struct{})
		var applied, requested LC.Tun
		var observation TunObservation
		go func() {
			applied, requested, observation = GetTunConf(), GetLastTunConf(), CurrentTunObservation()
			close(read)
		}()
		waitTunSignal(t, read)
		if applied.Enable || requested.MTU != expectedMTU || !observation.Stale || observation.Tun.State != "unavailable" {
			t.Fatalf("native transition advertised stale readiness: applied=%+v requested_mtu=%d observation=%+v", applied, requested.MTU, observation)
		}
		if TryObserveTunHealth() {
			t.Fatal("OS observer entered a concurrent native operation")
		}
	}
	waitTunSignal(t, closeEntered)
	assertResponsive(live.config.MTU)
	closeOnce.Do(func() { close(closeRelease) })
	waitTunSignal(t, createEntered)
	assertResponsive(wanted.MTU)
	createOnce.Do(func() { close(createRelease) })
	waitTunSignal(t, done)
	if operationErr != nil || !GetTunConf().Enable || CurrentTunObservation().CreationCount != 1 {
		t.Fatalf("replacement failed after blocked calls returned: err=%v observation=%+v", operationErr, CurrentTunObservation())
	}
	read := GetTunConf()
	read.Inet4Address[0] = netip.MustParsePrefix("10.20.30.40/32")
	if GetTunConf().Inet4Address[0] != wanted.Inet4Address[0] {
		t.Fatal("applied snapshot exposes mutable configuration slices")
	}
}

func TestTunFailedReplacementRemainsKnownAfterLostResult(t *testing.T) {
	live := setupTunHealth(t)
	missing := true
	tunInterfaceByName = func(name string) (*net.Interface, error) {
		if missing {
			return nil, errTunDeviceMissing
		}
		return &net.Interface{Name: name, Index: 43, Flags: net.FlagUp}, nil
	}
	failed := errors.New("transient driver creation failure")
	attempts := 0
	newTunListener = func(LC.Tun, C.Tunnel) (tunListener, error) { attempts++; return nil, failed }
	for i := 0; i < 2; i++ {
		state, repaired, err := RecoverTun(nil)
		if !errors.Is(err, failed) || repaired || state.State != "degraded" || !state.Enabled || !state.Repairable || state.Device != "" {
			t.Fatalf("failure was erased on request %d: state=%+v repaired=%v err=%v", i, state, repaired, err)
		}
	}
	if attempts != 1 || live.closed != 1 || GetTunConf().Enable || !GetLastTunConf().Enable {
		t.Fatalf("failed operation retried or lost requested/applied distinction: attempts=%d closed=%d", attempts, live.closed)
	}
	if stale := currentTunObservationAt(time.Now().Add(time.Hour)); stale.Stale || stale.Tun.State != "degraded" || !stale.Tun.Repairable {
		t.Fatalf("known lifecycle failure aged into uncertainty: %+v", stale)
	}
	// A later explicit configuration application owns a fresh attempt and can
	// clear the failure. Retrying an old recovery request above could not.
	newTunListener = func(cfg LC.Tun, _ C.Tunnel) (tunListener, error) {
		attempts++
		missing = false
		return &healthTestTun{fakeTunListener: &fakeTunListener{config: cfg, device: "test-tun", index: 43}, owned: true, routes: true}, nil
	}
	if err := ReCreateTun(GetLastTunConf(), nil); err != nil || InspectTunHealth().State != "healthy" || attempts != 2 || tunFailure != nil {
		t.Fatalf("explicit fresh application did not resolve failure: err=%v attempts=%d state=%+v", err, attempts, CurrentTunObservation())
	}
}

func TestTunCancelledConstructorClosesLateDeviceBeforePublication(t *testing.T) {
	setupTunHealth(t)
	missing := true
	tunInterfaceByName = func(name string) (*net.Interface, error) {
		if missing {
			return nil, errTunDeviceMissing
		}
		return &net.Interface{Name: name, Index: 43, Flags: net.FlagUp}, nil
	}
	entered, release, done := make(chan struct{}), make(chan struct{}), make(chan struct{})
	var releaseOnce sync.Once
	var late *healthTestTun
	newTunListener = func(cfg LC.Tun, _ C.Tunnel) (tunListener, error) {
		close(entered)
		<-release
		missing = false
		late = &healthTestTun{fakeTunListener: &fakeTunListener{config: cfg, device: "test-tun", index: 43}, owned: true, routes: true}
		return late, nil
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	var operationErr error
	go func() { _, _, operationErr = RecoverTunContext(ctx, nil); close(done) }()
	t.Cleanup(func() { cancel(); releaseOnce.Do(func() { close(release) }); waitTunSignal(t, done) })
	waitTunSignal(t, entered)
	cancel()
	releaseOnce.Do(func() { close(release) })
	waitTunSignal(t, done)
	if !errors.Is(operationErr, context.Canceled) || late == nil || late.closed != 1 || tunLister != nil || GetTunConf().Enable {
		t.Fatalf("cancelled creation escaped cleanup: err=%v late=%+v applied=%+v", operationErr, late, GetTunConf())
	}
	if CurrentTunObservation().Tun.State == "healthy" || CurrentTunObservation().CreationCount != 1 {
		t.Fatalf("late device was published healthy: %+v", CurrentTunObservation())
	}
}

func TestTunInPlaceRepairFailureKeepsOwnedDevice(t *testing.T) {
	live := setupTunHealth(t)
	live.routes, live.repairErr = false, errors.New("route transaction failed")
	state, repaired, err := RecoverTun(nil)
	if !errors.Is(err, live.repairErr) || repaired || state.State != "degraded" || tunLister != live || live.closed != 0 || live.repairs != 1 || CurrentTunObservation().CreationCount != 0 {
		t.Fatalf("in-place failure discarded the intact device: state=%+v repaired=%v err=%v listener=%+v", state, repaired, err, live)
	}
}

func TestTunObservationExpiresWithoutChangingObservedTime(t *testing.T) {
	setupTunHealth(t)
	InspectTunHealth()
	fresh := CurrentTunObservation()
	stale := currentTunObservationAt(time.Now().Add(tunObservationMaxAge + time.Second))
	if fresh.Stale || fresh.Tun.State != "healthy" || !stale.Stale || stale.Tun.State != "unavailable" || stale.Tun.Repairable || stale.ObservedAt != fresh.ObservedAt {
		t.Fatalf("stale snapshot masqueraded as a fresh probe: fresh=%+v stale=%+v", fresh, stale)
	}
}
