package executor

import (
	"context"
	"errors"
	"runtime"
	"strconv"
	"sync"
	"time"

	"github.com/metacubex/mihomo/listener"
	"github.com/metacubex/mihomo/tunnel"
)

var ErrRecoveryBusy = errors.New("configuration or recovery is already in progress")
var ErrCoreNotReady = errors.New("core configuration is not ready")

type DesktopRecovery struct {
	Status string             `json:"status"`
	Tun    listener.TunHealth `json:"tun"`
	Error  string             `json:"error,omitempty"`
}

type DesktopObservation struct {
	listener.TunObservation
	CoreInstance string                    `json:"core_instance"`
	Recovery     *DesktopRecoveryOperation `json:"recovery"`
}

var (
	desktopHealthOnce     sync.Once
	desktopHealthRequests = make(chan struct{}, 1)
)

const desktopHealthInterval = 2 * time.Second

// Only one OS observer exists for the process. Requests coalesce, never spawn
// per-request goroutines, and never queue on the lifecycle mutex. A native
// enumeration that stalls therefore cannot also stall controller reads.
func requestDesktopHealthObservation() {
	desktopHealthOnce.Do(func() {
		go func() {
			ticker := time.NewTicker(desktopHealthInterval)
			defer ticker.Stop()
			runDesktopHealthObserver(desktopHealthRequests, ticker.C, time.Now, listener.TryObserveTunHealth, ShutdownRequested)
		}()
	})
	select {
	case desktopHealthRequests <- struct{}{}:
	default:
	}
}

// Refresh independently of the desktop's slower health polling cadence. With
// demand-only refresh, a 10-second client poll would always find the 5-second
// cache expired and unnecessarily enter recovery on every healthy idle poll.
// Native calls stay on this one worker; queued ticks and requests coalesce.
func runDesktopHealthObserver(requests <-chan struct{}, ticks <-chan time.Time, now func() time.Time, observe func() bool, stopping func() bool) {
	var attempted time.Time
	for {
		select {
		case <-requests:
		case <-ticks:
		}
		if stopping() {
			return
		}
		current := now()
		if !attempted.IsZero() && current.Sub(attempted) < time.Second {
			continue
		}
		attempted = current
		observe()
	}
}

func InspectDesktopObservation(ctx context.Context) (DesktopObservation, error) {
	if err := ctx.Err(); err != nil {
		return DesktopObservation{}, err
	}
	if ShutdownRequested() {
		return DesktopObservation{}, ErrShuttingDown
	}
	requestDesktopHealthObservation()
	observation := desktopRecoveries.observe()
	operation := desktopRecoveries.current(observation)
	if operation != nil && operation.Phase == "failed" && operation.Status == "needs_restart" && observation.Tun.Enabled {
		// A v1 supervisor only submits recovery for a repairable degraded TUN.
		// Keep a timed-out owned native operation actionable even though its OS
		// observation is stale; otherwise it would observe "unavailable" forever.
		observation.Tun.State, observation.Tun.Repairable = "degraded", true
		observation.Tun.Reason = "owned TUN recovery failed: " + operation.Error
	}
	return DesktopObservation{
		TunObservation: observation, CoreInstance: desktopCoreInstance,
		Recovery: operation,
	}, nil
}

func InspectDesktopHealth(ctx context.Context) (listener.TunHealth, error) {
	observation, err := InspectDesktopObservation(ctx)
	return observation.Tun, err
}

// RecoverDesktop retains the v1 empty-POST contract and existing enum values.
// Busy is immediate while owned native work runs. Terminal failures survive a
// lost response and are returned repeatedly instead of disappearing as unknown.
func RecoverDesktop(ctx context.Context) (DesktopRecovery, error) {
	if err := ctx.Err(); err != nil {
		return DesktopRecovery{}, err
	}
	if ShutdownRequested() {
		return DesktopRecovery{}, ErrShuttingDown
	}
	m := desktopRecoveries
	if !m.ready() {
		return DesktopRecovery{}, ErrCoreNotReady
	}
	observation := m.observe()
	m.mu.Lock()
	if m.active != nil {
		operation := m.active.snapshot
		m.mu.Unlock()
		if operation.Phase != "running" {
			return recoveryResult(operation), nil
		}
		return DesktopRecovery{}, ErrRecoveryBusy
	}
	if m.latest != nil && sameTunGeneration(m.latest.result, observation) &&
		m.latest.snapshot.Status == "needs_restart" && observation.Tun.State != "healthy" && observation.Tun.State != "disabled" {
		result := recoveryResult(m.latest.snapshot)
		m.mu.Unlock()
		return result, nil
	}
	m.sequence++
	requestID := "legacy-" + strconv.FormatUint(m.sequence, 10)
	m.mu.Unlock()
	// A successful operation must not mask a later missing route on the same
	// device. A fresh healthy observation, however, needs no mutation at all.
	if !observation.Stale && (observation.Tun.State == "healthy" || observation.Tun.State == "disabled") {
		if !mux.TryLock() {
			return DesktopRecovery{}, ErrRecoveryBusy
		}
		mux.Unlock()
		return DesktopRecovery{Status: "healthy", Tun: observation.Tun}, nil
	}
	if !m.supported {
		return DesktopRecovery{Status: "unavailable", Tun: observation.Tun, Error: "desktop TUN recovery is only supported on Windows"}, nil
	}
	_, err := m.start(ctx, DesktopRecoveryRequest{RequestID: requestID,
		ConfigGeneration: observation.ConfigGeneration, TunGeneration: observation.TunGeneration})
	if err != nil {
		return DesktopRecovery{}, err
	}
	return DesktopRecovery{}, ErrRecoveryBusy
}

func runDesktopRecovery(ctx context.Context) (DesktopRecovery, error) {
	if err := ctx.Err(); err != nil {
		return DesktopRecovery{Status: "unavailable", Tun: listener.CurrentTunObservation().Tun}, err
	}
	if ShutdownRequested() {
		return DesktopRecovery{Status: "unavailable", Tun: listener.CurrentTunObservation().Tun}, ErrShuttingDown
	}
	if runtime.GOOS != "windows" {
		return DesktopRecovery{Status: "unavailable", Tun: listener.CurrentTunObservation().Tun,
			Error: "desktop TUN recovery is only supported on Windows"}, nil
	}
	state, repaired, err := listener.RecoverTunContext(ctx, tunnel.Tunnel)
	result := DesktopRecovery{Status: "healthy", Tun: state}
	if err != nil {
		result.Error = err.Error()
	}
	if ctx.Err() != nil {
		result.Status = "unavailable"
		return result, ctx.Err()
	}
	switch state.State {
	case "conflict":
		result.Status = "conflict"
	case "unavailable":
		result.Status = "unavailable"
	case "degraded":
		result.Status = "needs_restart"
	case "healthy", "disabled":
		if err != nil {
			result.Status = "needs_restart"
		} else if repaired {
			result.Status = "repaired"
		}
	default:
		result.Status = "unavailable"
	}
	if result.Status == "healthy" || result.Status == "repaired" {
		tunnel.ResumeLANPreconnect()
	}
	return result, nil
}
