package executor

import (
	"context"
	"errors"
	"runtime"

	"github.com/metacubex/mihomo/component/desktop"
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

// TryLock keeps the supervisor's heartbeat responsive during a slow reload.
// The caller retries busy results; it must not interpret them as process death.
func InspectDesktopHealth(ctx context.Context) (listener.TunHealth, error) {
	if err := ctx.Err(); err != nil {
		return listener.TunHealth{}, err
	}
	if !mux.TryLock() {
		return listener.TunHealth{}, ErrRecoveryBusy
	}
	defer mux.Unlock()
	if ShutdownRequested() {
		return listener.TunHealth{}, ErrShuttingDown
	}
	return listener.InspectTunHealth(), nil
}

// RecoverDesktop does not parse configuration, download subscriptions or change
// readiness to wait for a remote LAN. The existing running instance is retained
// whenever its local resources are healthy.
func RecoverDesktop(ctx context.Context) (DesktopRecovery, error) {
	if err := ctx.Err(); err != nil {
		return DesktopRecovery{}, err
	}
	if !mux.TryLock() {
		return DesktopRecovery{}, ErrRecoveryBusy
	}
	defer mux.Unlock()
	if ShutdownRequested() {
		return DesktopRecovery{}, ErrShuttingDown
	}
	if !desktop.Ready() {
		return DesktopRecovery{}, ErrCoreNotReady
	}
	if err := ctx.Err(); err != nil {
		return DesktopRecovery{}, err
	}
	if runtime.GOOS != "windows" {
		return DesktopRecovery{Status: "unavailable", Tun: listener.InspectTunHealth(), Error: "desktop TUN recovery is only supported on Windows"}, nil
	}
	state, repaired, err := listener.RecoverTun(tunnel.Tunnel)
	result := DesktopRecovery{Status: "healthy", Tun: state}
	if err != nil {
		result.Error = err.Error()
	}
	switch state.State {
	case "conflict":
		result.Status = "conflict"
	case "unavailable":
		result.Status = "unavailable"
		if err != nil && state.Enabled && state.Device == "" {
			// Replacement failed after the old listener was already released.
			// The supervisor may restart its own process using its normal limits.
			result.Status = "needs_restart"
		}
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
