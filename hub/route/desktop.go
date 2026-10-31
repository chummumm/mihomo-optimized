package route

import (
	"errors"
	"io"
	"os"
	"runtime"
	"strconv"

	"github.com/metacubex/mihomo/component/desktop"
	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/hub/executor"

	"github.com/metacubex/chi"
	"github.com/metacubex/chi/render"
	"github.com/metacubex/http"
)

func desktopRouter() http.Handler {
	r := chi.NewRouter()
	r.Get("/", getDesktopStatus)
	r.Get("/health", getDesktopHealth)
	r.Post("/recover", recoverDesktop)
	r.Post("/shutdown", shutdownDesktop)
	return r
}

func getDesktopStatus(w http.ResponseWriter, r *http.Request) {
	state := "loading"
	if desktop.Ready() {
		state = "running"
	}
	if executor.ShutdownRequested() {
		state = "stopping"
	}
	address, _ := desktop.Controller()
	w.Header().Set("Cache-Control", "no-store")
	render.JSON(w, r, render.M{
		"managed": true, "api_version": 1, "state": state,
		"pid": os.Getpid(), "platform": runtime.GOOS, "arch": runtime.GOARCH,
		"core_version": C.Version, "controller": address, "config_path": C.Path.Config(),
		"update_owner": "desktop",
		"capabilities": render.M{
			"config_edit": true, "config_reload": true, "tun": true,
			"rule_updates": true, "geodata_updates": true,
			"core_update": false, "ui_update": false, "self_restart": false,
			"shutdown": true, "unified_update_scope": "rules_and_geodata",
			"tun_health": runtime.GOOS == "windows", "tun_repair": runtime.GOOS == "windows",
		},
	})
}

func getDesktopHealth(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	health, err := executor.InspectDesktopHealth(r.Context())
	if err != nil {
		renderDesktopRecoveryError(w, r, err)
		return
	}
	state := "loading"
	if desktop.Ready() {
		state = "running"
	}
	if executor.ShutdownRequested() {
		state = "stopping"
	}
	render.JSON(w, r, render.M{"managed": true, "api_version": 1, "pid": os.Getpid(), "state": state, "tun": health})
}

func recoverDesktop(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	body, err := io.ReadAll(io.LimitReader(r.Body, 1))
	if err != nil || len(body) != 0 || r.URL.RawQuery != "" {
		render.Status(r, http.StatusBadRequest)
		render.JSON(w, r, newError("recovery requires an empty request"))
		return
	}
	result, err := executor.RecoverDesktop(r.Context())
	if err != nil {
		renderDesktopRecoveryError(w, r, err)
		return
	}
	render.JSON(w, r, render.M{"pid": os.Getpid(), "status": result.Status, "tun": result.Tun, "error": result.Error})
}

func renderDesktopRecoveryError(w http.ResponseWriter, r *http.Request, err error) {
	status := http.StatusServiceUnavailable
	if errors.Is(err, executor.ErrShuttingDown) || errors.Is(err, executor.ErrCoreNotReady) {
		status = http.StatusConflict
	} else {
		w.Header().Set("Retry-After", "1")
	}
	render.Status(r, status)
	render.JSON(w, r, newError(err.Error()))
}

func shutdownDesktop(w http.ResponseWriter, r *http.Request) {
	// This router is mounted inside the authenticated controller group only.
	body, err := io.ReadAll(io.LimitReader(r.Body, 1))
	if err != nil || len(body) != 0 || r.URL.RawQuery != "" {
		render.Status(r, http.StatusBadRequest)
		render.JSON(w, r, newError("shutdown requires an empty request"))
		return
	}
	// Close configuration admission first; publish process exit only after the
	// complete acknowledgement has been flushed. main owns cleanup and retries.
	executor.RequestShutdown()
	const response = "{\"status\":\"stopping\"}\n"
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Content-Length", strconv.Itoa(len(response)))
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusAccepted)
	_, _ = w.Write([]byte(response))
	if f, ok := w.(http.Flusher); ok {
		f.Flush()
	}
	desktop.RequestShutdown()
}

func rejectDesktopOperation(w http.ResponseWriter, r *http.Request) bool {
	if !desktop.Enabled() {
		return false
	}
	render.Status(r, http.StatusConflict)
	render.JSON(w, r, render.M{"error": desktop.ErrManaged.Error(), "code": "desktop_managed", "update_owner": "desktop"})
	return true
}
