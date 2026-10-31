package listener

import (
	"errors"
	"net"
	"net/netip"
	"testing"

	C "github.com/metacubex/mihomo/constant"
	LC "github.com/metacubex/mihomo/listener/config"
)

type healthTestTun struct {
	*fakeTunListener
	owned    bool
	routes   bool
	probeErr error
}

func (l *healthTestTun) CheckDeviceIdentity() (bool, error) { return l.owned, nil }
func (l *healthTestTun) CheckRoutes() (bool, error)         { return l.routes, l.probeErr }

func setupTunHealth(t *testing.T) *healthTestTun {
	t.Helper()
	isolateTransparentListeners(t)
	LastTunConf = LC.Tun{Enable: true, Device: "test-tun", AutoRoute: true,
		Inet4Address: []netip.Prefix{netip.MustParsePrefix("172.19.0.1/30")},
		Inet6Address: []netip.Prefix{netip.MustParsePrefix("fd00::1/126")}}
	live := &healthTestTun{fakeTunListener: &fakeTunListener{config: LastTunConf.Clone(), device: "test-tun", index: 42}, owned: true, routes: true}
	tunLister = live
	tunInterfaceAddresses = func(*net.Interface) ([]net.Addr, error) {
		return []net.Addr{&net.IPNet{IP: net.ParseIP("172.19.0.1"), Mask: net.CIDRMask(30, 32)},
			&net.IPNet{IP: net.ParseIP("fd00::1"), Mask: net.CIDRMask(126, 128)}}, nil
	}
	return live
}

func TestDesktopTunHealthPreservesHealthyDevice(t *testing.T) {
	live := setupTunHealth(t)
	for i := 0; i < 3; i++ {
		state, repaired, err := RecoverTun(nil)
		if err != nil || repaired || state.State != "healthy" || live.closed != 0 || live.reloads != 0 {
			t.Fatalf("healthy listener changed: state=%+v repaired=%v err=%v closed=%d reloads=%d", state, repaired, err, live.closed, live.reloads)
		}
	}
}

func TestDesktopTunHealthRejectsForeignIdentity(t *testing.T) {
	for _, changedIndex := range []bool{false, true} {
		t.Run(map[bool]string{false: "recycled-index-new-luid", true: "changed-index"}[changedIndex], func(t *testing.T) {
			live := setupTunHealth(t)
			if changedIndex {
				tunInterfaceByName = func(name string) (*net.Interface, error) {
					return &net.Interface{Name: name, Index: 43, Flags: net.FlagUp}, nil
				}
			} else {
				live.owned = false
			}
			state, repaired, err := RecoverTun(nil)
			if err != nil || repaired || state.State != "conflict" || live.closed != 0 {
				t.Fatalf("foreign listener was not preserved: %+v repaired=%v err=%v closed=%d", state, repaired, err, live.closed)
			}
		})
	}
}

func TestDesktopTunHealthRequiresEveryAddress(t *testing.T) {
	live := setupTunHealth(t)
	live.config.Inet4Address = append(live.config.Inet4Address, netip.MustParsePrefix("172.20.0.1/30"))
	state := InspectTunHealth()
	if state.State != "degraded" || !state.Repairable || state.Reason != "owned TUN address is missing" {
		t.Fatalf("one present address hid a missing configured address: %+v", state)
	}
}

func TestDesktopTunHealthInspectionFailureDoesNotRecreate(t *testing.T) {
	live := setupTunHealth(t)
	live.probeErr = errors.New("temporary route query failure")
	state, repaired, err := RecoverTun(nil)
	if err != nil || repaired || state.State != "unavailable" || state.Repairable || live.closed != 0 {
		t.Fatalf("failed inspection authorized mutation: %+v repaired=%v err=%v closed=%d", state, repaired, err, live.closed)
	}
}

func TestDesktopTunHealthRepairsMissingRoute(t *testing.T) {
	live := setupTunHealth(t)
	live.routes = false
	created := 0
	newTunListener = func(cfg LC.Tun, _ C.Tunnel) (tunListener, error) {
		created++
		return &healthTestTun{fakeTunListener: &fakeTunListener{config: cfg, device: "test-tun", index: 42}, owned: true, routes: true}, nil
	}
	state, repaired, err := RecoverTun(nil)
	if err != nil || !repaired || state.State != "healthy" || created != 1 || live.closed != 1 {
		t.Fatalf("missing route recovery failed: %+v repaired=%v err=%v created=%d closed=%d", state, repaired, err, created, live.closed)
	}
}

func TestDesktopTunHealthRepairsMissingDevice(t *testing.T) {
	live := setupTunHealth(t)
	missing := true
	tunInterfaceByName = func(name string) (*net.Interface, error) {
		if missing {
			return nil, errTunDeviceMissing
		}
		return &net.Interface{Name: name, Index: 43, Flags: net.FlagUp}, nil
	}
	newTunListener = func(cfg LC.Tun, _ C.Tunnel) (tunListener, error) {
		missing = false
		return &healthTestTun{fakeTunListener: &fakeTunListener{config: cfg, device: "test-tun", index: 43}, owned: true, routes: true}, nil
	}
	state, repaired, err := RecoverTun(nil)
	if err != nil || !repaired || state.State != "healthy" || state.Index != 43 || live.closed != 1 {
		t.Fatalf("missing device recovery failed: %+v repaired=%v err=%v", state, repaired, err)
	}
}

func TestDesktopTunHealthDoesNotCreateDisabledDevice(t *testing.T) {
	isolateTransparentListeners(t)
	state, repaired, err := RecoverTun(nil)
	if err != nil || repaired || state.State != "disabled" || state.Enabled {
		t.Fatalf("disabled TUN was activated: %+v repaired=%v err=%v", state, repaired, err)
	}
}

func TestDesktopTunHealthChecksIdentityAgainBeforeClose(t *testing.T) {
	live := setupTunHealth(t)
	live.routes = false
	queries := 0
	tunInterfaceByName = func(name string) (*net.Interface, error) {
		queries++
		index := 42
		if queries >= 2 {
			index = 43
		}
		return &net.Interface{Name: name, Index: index, Flags: net.FlagUp}, nil
	}
	_, _, err := RecoverTun(nil)
	if err == nil || live.closed != 0 {
		t.Fatalf("device replacement during recovery was not protected: err=%v closed=%d", err, live.closed)
	}
}
