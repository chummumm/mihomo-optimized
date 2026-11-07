package listener

import (
	"sync/atomic"
	"time"

	LC "github.com/metacubex/mihomo/listener/config"
)

const tunObservationMaxAge = 5 * time.Second

// TunObservation separates the requested configuration, the device lifetime,
// and a time-bounded observation of operating-system resources. Generations
// change only under transparentListenersMu; readers never acquire that lock.
type TunObservation struct {
	Tun              TunHealth `json:"tun"`
	ConfigGeneration uint64    `json:"config_generation"`
	TunGeneration    uint64    `json:"tun_generation"`
	CreationCount    uint64    `json:"creation_count"`
	ObservedAt       string    `json:"observed_at"`
	Stale            bool      `json:"stale"`
}

type tunSnapshot struct {
	requested        LC.Tun
	applied          LC.Tun
	health           TunHealth
	observedAt       time.Time
	configGeneration uint64
	tunGeneration    uint64
	creationCount    uint64
	transition       string
	knownFailure     bool
}

var publishedTun atomic.Pointer[tunSnapshot]

// All mutable lifecycle facts below are protected by transparentListenersMu.
// A failed replacement remains a known failure until a later configuration
// application succeeds. A missing pointer must not erase that evidence.
var (
	tunConfigGeneration uint64
	tunGeneration       uint64
	tunCreationCount    uint64
	tunTransition       string
	tunFailure          error
)

func currentTunSnapshot() tunSnapshot {
	if snapshot := publishedTun.Load(); snapshot != nil {
		return *snapshot
	}
	return tunSnapshot{health: TunHealth{State: "disabled"}}
}

// CurrentTunObservation is safe even while a driver call or route enumeration
// is blocked. A cached healthy result expires; a known failed replacement does
// not silently turn into an inconclusive enumeration result as it ages.
func CurrentTunObservation() TunObservation {
	return currentTunObservationAt(time.Now())
}

func currentTunObservationAt(now time.Time) TunObservation {
	snapshot := currentTunSnapshot()
	result := TunObservation{
		Tun: snapshot.health, ConfigGeneration: snapshot.configGeneration,
		TunGeneration: snapshot.tunGeneration, CreationCount: snapshot.creationCount,
	}
	if !snapshot.observedAt.IsZero() {
		result.ObservedAt = snapshot.observedAt.UTC().Format(time.RFC3339Nano)
	}
	result.Stale = snapshot.transition != "" || snapshot.health.Enabled && !snapshot.knownFailure &&
		(snapshot.observedAt.IsZero() || now.Sub(snapshot.observedAt) > tunObservationMaxAge)
	if result.Stale && result.Tun.Enabled {
		result.Tun.State, result.Tun.Repairable = "unavailable", false
		result.Tun.Reason = "TUN health observation is pending"
		if snapshot.transition != "" {
			result.Tun.Reason = "TUN operation is in progress: " + snapshot.transition
		} else if !snapshot.observedAt.IsZero() {
			result.Tun.Reason = "TUN health observation is stale"
		}
	}
	return result
}

// publishTunStateLocked publishes only facts already known by the lifecycle
// owner. It deliberately performs no operating-system inspection. The applied
// configuration is withdrawn before Close, rather than after a slow native
// call finally returns.
func publishTunStateLocked() {
	snapshot := &tunSnapshot{
		requested: LastTunConf.Clone(), applied: LastTunConf.Clone(),
		configGeneration: tunConfigGeneration, tunGeneration: tunGeneration,
		creationCount: tunCreationCount, transition: tunTransition,
		health: TunHealth{Enabled: LastTunConf.Enable, State: "disabled"},
	}
	snapshot.applied.Enable = false
	if tunLister != nil {
		snapshot.health.Device, snapshot.health.Index = tunLister.DeviceName(), tunLister.DeviceIndex()
		if identity, ok := tunLister.(interface{ DeviceIdentity() string }); ok {
			snapshot.health.Identity = identity.DeviceIdentity()
		}
		if tunTransition != "closing" {
			snapshot.applied = tunLister.Config().Clone()
		}
	}
	if LastTunConf.Enable {
		snapshot.health.State, snapshot.health.Reason = "unavailable", "TUN health observation is pending"
		if tunLister == nil && tunFailure != nil && tunTransition == "" {
			snapshot.knownFailure = true
			snapshot.health.State, snapshot.health.Repairable = "degraded", true
			snapshot.health.Reason = "owned TUN replacement failed: " + tunFailure.Error()
			snapshot.observedAt = time.Now()
		}
	} else {
		snapshot.observedAt = time.Now()
	}
	publishedTun.Store(snapshot)
}

func publishTunObservationLocked(health TunHealth) {
	publishTunStateLocked()
	snapshot := currentTunSnapshot()
	snapshot.health = health
	snapshot.observedAt = time.Now()
	publishedTun.Store(&snapshot)
}

// TryObserveTunHealth is called by the single coalescing observer. Lock
// contention is not a failed probe, and neither readers nor extra observers
// queue behind a slow replacement. Native inspection remains serialized with
// close so it can never examine an already-released listener.
func TryObserveTunHealth() bool {
	if !transparentListenersMu.TryLock() {
		return false
	}
	defer transparentListenersMu.Unlock()
	publishTunObservationLocked(inspectTunHealthLocked())
	return true
}
