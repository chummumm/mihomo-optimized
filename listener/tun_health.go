package listener

import (
	"errors"
	"fmt"
	"net"
	"net/netip"

	C "github.com/metacubex/mihomo/constant"
)

// TunHealth describes local resources owned by this listener. It makes no
// claim about Internet connectivity or a remote proxy's availability.
type TunHealth struct {
	Enabled    bool   `json:"enabled"`
	State      string `json:"state"`
	Device     string `json:"device"`
	Index      int    `json:"index"`
	Reason     string `json:"reason,omitempty"`
	Repairable bool   `json:"repairable"`
}

func InspectTunHealth() TunHealth {
	transparentListenersMu.Lock()
	defer transparentListenersMu.Unlock()
	return inspectTunHealthLocked()
}

func inspectTunHealthLocked() TunHealth {
	state := TunHealth{Enabled: LastTunConf.Enable, State: "disabled"}
	if !state.Enabled {
		return state
	}
	state.State = "unavailable"
	if tunLister == nil {
		state.Reason = "owned TUN listener is unavailable"
		return state
	}
	state.Device, state.Index = tunLister.DeviceName(), tunLister.DeviceIndex()
	if state.Device == "" || state.Index <= 0 {
		state.Reason = "original TUN identity is unavailable"
		return state
	}
	iface, err := tunInterfaceByName(state.Device)
	if errors.Is(err, errTunDeviceMissing) {
		state.State, state.Reason, state.Repairable = "degraded", "owned TUN device is missing", true
		return state
	}
	if err != nil {
		state.Reason = fmt.Sprintf("inspect TUN device: %v", err)
		return state
	}
	if iface == nil || iface.Name != state.Device || iface.Index != state.Index {
		state.State, state.Reason = "conflict", "TUN device identity changed"
		return state
	}
	if guard, ok := tunLister.(interface{ CheckDeviceIdentity() (bool, error) }); ok {
		owned, err := guard.CheckDeviceIdentity()
		if err != nil {
			state.Reason = fmt.Sprintf("inspect TUN identity: %v", err)
			return state
		}
		if !owned {
			state.State, state.Reason = "conflict", "TUN device identity changed"
			return state
		}
	}
	state.State, state.Repairable = "degraded", true
	if iface.Flags&net.FlagUp == 0 {
		state.Reason = "owned TUN device is down"
		return state
	}
	options := tunLister.Config()
	addresses, err := tunInterfaceAddresses(iface)
	if err != nil {
		state.State, state.Repairable = "unavailable", false
		state.Reason = fmt.Sprintf("inspect TUN addresses: %v", err)
		return state
	}
	if !tunHasAllAddresses(addresses, options.Inet4Address, options.Inet6Address) {
		state.Reason = "owned TUN address is missing"
		return state
	}
	if options.AutoRoute {
		probe, ok := tunLister.(interface{ CheckRoutes() (bool, error) })
		if !ok {
			state.State, state.Reason, state.Repairable = "unavailable", "TUN route inspection is unavailable", false
			return state
		}
		ready, err := probe.CheckRoutes()
		if err != nil {
			state.State, state.Repairable = "unavailable", false
			state.Reason = fmt.Sprintf("inspect TUN routes: %v", err)
			return state
		}
		if !ready {
			state.Reason = "owned TUN route is missing"
			return state
		}
	}
	state.State, state.Reason, state.Repairable = "healthy", "", false
	return state
}

func tunHasAllAddresses(addresses []net.Addr, families ...[]netip.Prefix) bool {
	actual := make(map[netip.Prefix]struct{}, len(addresses))
	for _, address := range addresses {
		if prefix, err := netip.ParsePrefix(address.String()); err == nil {
			actual[prefix] = struct{}{}
		}
	}
	for _, requested := range families {
		for _, prefix := range requested {
			if _, exists := actual[prefix]; !exists {
				return false
			}
		}
	}
	return true
}

// RecoverTun repairs a verified local failure while retaining healthy listeners.
// The caller serializes this operation with configuration application and stop.
// Inspection failures and foreign identities never authorize a replacement.
func RecoverTun(tunnel C.Tunnel) (TunHealth, bool, error) {
	transparentListenersMu.Lock()
	defer transparentListenersMu.Unlock()
	before := inspectTunHealthLocked()
	if !before.Repairable {
		return before, false, nil
	}
	if err := recreateTunLocked(LastTunConf, tunnel, true); err != nil {
		return inspectTunHealthLocked(), false, err
	}
	after := inspectTunHealthLocked()
	if after.State != "healthy" && after.State != "disabled" {
		return after, true, fmt.Errorf("TUN recovery did not restore local resources: %s", after.Reason)
	}
	return after, true, nil
}
