package listener

import (
	"context"
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
	Identity   string `json:"identity,omitempty"`
	Reason     string `json:"reason,omitempty"`
	Repairable bool   `json:"repairable"`
}

func InspectTunHealth() TunHealth {
	transparentListenersMu.Lock()
	defer transparentListenersMu.Unlock()
	state := inspectTunHealthLocked()
	publishTunObservationLocked(state)
	return state
}

const (
	tunAddressNotReadyReason = "owned TUN address is missing or not ready"
	tunRouteMissingReason    = "owned TUN route is missing"
)

func inspectTunHealthLocked() TunHealth {
	state := TunHealth{Enabled: LastTunConf.Enable, State: "disabled"}
	if !state.Enabled {
		return state
	}
	state.State = "unavailable"
	if tunLister == nil {
		state.Reason = "owned TUN listener is unavailable"
		if tunFailure != nil {
			state.State, state.Repairable = "degraded", true
			state.Reason = "owned TUN replacement failed: " + tunFailure.Error()
		}
		return state
	}
	state.Device, state.Index = tunLister.DeviceName(), tunLister.DeviceIndex()
	if identity, ok := tunLister.(interface{ DeviceIdentity() string }); ok {
		state.Identity = identity.DeviceIdentity()
	}
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
	if probe, ok := tunLister.(interface{ CheckAddresses() (bool, error) }); ok {
		ready, err := probe.CheckAddresses()
		if err != nil {
			state.State, state.Repairable = "unavailable", false
			state.Reason = fmt.Sprintf("inspect TUN addresses: %v", err)
			return state
		}
		if !ready {
			state.Reason = tunAddressNotReadyReason
			return state
		}
	} else {
		addresses, err := tunInterfaceAddresses(iface)
		if err != nil {
			state.State, state.Repairable = "unavailable", false
			state.Reason = fmt.Sprintf("inspect TUN addresses: %v", err)
			return state
		}
		if !tunHasAllAddresses(addresses, options.Inet4Address, options.Inet6Address) {
			state.Reason = tunAddressNotReadyReason
			return state
		}
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
			state.Reason = tunRouteMissingReason
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
	return RecoverTunContext(context.Background(), tunnel)
}

// The operation owner supplies this context; an HTTP client's lifetime must
// not cancel accepted work. Cancellation never abandons a native constructor:
// its returned device is closed before the serialized operation can finish.
func RecoverTunContext(ctx context.Context, tunnel C.Tunnel) (TunHealth, bool, error) {
	transparentListenersMu.Lock()
	defer transparentListenersMu.Unlock()
	if err := ctx.Err(); err != nil {
		return CurrentTunObservation().Tun, false, err
	}
	before := inspectTunHealthLocked()
	publishTunObservationLocked(before)
	if !before.Repairable {
		return before, false, nil
	}
	if tunLister == nil && tunFailure != nil {
		// A replacement already consumed the old device. Preserve its failure
		// for late/retried HTTP requests instead of trying another constructor.
		return before, false, tunFailure
	}
	if before.Reason == tunAddressNotReadyReason || before.Reason == tunRouteMissingReason {
		repair, ok := tunLister.(interface{ RepairResources(context.Context) error })
		if !ok {
			return before, false, errors.New("owned TUN resource repair is unavailable")
		}
		tunTransition = "repairing resources"
		publishTunStateLocked()
		err := repair.RepairResources(ctx)
		tunTransition = ""
		if ctx.Err() != nil {
			publishTunStateLocked()
			return CurrentTunObservation().Tun, false, errors.Join(err, ctx.Err())
		}
		after := inspectTunHealthLocked()
		publishTunObservationLocked(after)
		if err != nil {
			return after, false, err
		}
		if after.State != "healthy" {
			return after, false, fmt.Errorf("TUN resource repair did not restore local resources: %s", after.Reason)
		}
		return after, true, nil
	}
	if err := recreateTunContextLocked(ctx, LastTunConf, tunnel, true); err != nil {
		if ctx.Err() != nil {
			return CurrentTunObservation().Tun, false, err
		}
		after := inspectTunHealthLocked()
		publishTunObservationLocked(after)
		return after, false, err
	}
	after := inspectTunHealthLocked()
	publishTunObservationLocked(after)
	if after.State != "healthy" && after.State != "disabled" {
		return after, true, fmt.Errorf("TUN recovery did not restore local resources: %s", after.Reason)
	}
	return after, true, nil
}
