package route

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/metacubex/mihomo/component/desktop"
	"github.com/metacubex/mihomo/component/profile/cachefile"
	"github.com/metacubex/mihomo/component/resource"
	"github.com/metacubex/mihomo/component/updater"
	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/hub/executor"

	"github.com/metacubex/http"
	"github.com/metacubex/http/httptest"
)

// Managed mode is a one-way process policy. Exercise the real global paths in
// an isolated process rather than introducing a production reset/bypass API.
func TestDesktopManagedContracts(t *testing.T) {
	if os.Getenv("MIHOMO_DESKTOP_CONTRACT_CHILD") != "1" {
		executable, err := os.Executable()
		if err != nil {
			t.Fatal(err)
		}
		ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer cancel()
		cmd := exec.CommandContext(ctx, executable, "-test.run=^TestDesktopManagedContracts$", "-test.v")
		cmd.Env = append(os.Environ(), "MIHOMO_DESKTOP_CONTRACT_CHILD=1", "SKIP_SAFE_PATH_CHECK=true", "SAFE_PATHS="+os.TempDir())
		if output, err := cmd.CombinedOutput(); err != nil {
			t.Fatalf("managed contract process failed: %v\n%s", err, output)
		}
		return
	}

	home := t.TempDir()
	const secret = "desktop-contract-only-0123456789abcdefghijklmnopqrstuvwxyz"
	if err := desktop.Enable(desktop.Options{Home: home, Controller: "127.0.0.1:19871", Secret: secret}); err != nil {
		t.Fatal(err)
	}
	if err := desktop.Enable(desktop.Options{Home: home, Controller: "127.0.0.1:19872", Secret: secret}); err == nil {
		t.Fatal("process ownership policy was replaceable")
	}
	C.SetHomeDir(desktop.Home())
	C.SetConfig(filepath.Join(desktop.Home(), "config.yaml"))
	defer func() {
		if err := executor.Shutdown(); err != nil {
			t.Errorf("managed contract shutdown failed: %v", err)
		}
		// Unlike the daemon, this subprocess runs TempDir cleanup before it
		// exits. Release the mapped cache file first, as Windows requires.
		cache := cachefile.Cache()
		if cache.DB != nil {
			if err := cache.Close(); err != nil {
				t.Errorf("managed contract cache cleanup failed: %v", err)
			}
		}
	}()
	const yaml = "mixed-port: 0\nmode: direct\ncore-auto-update: true\nexternal-ui-auto-update: true\nexternal-controller: 0.0.0.0:12345\nexternal-controller-tls: 0.0.0.0:12346\nexternal-controller-unix: /untrusted/socket\nexternal-controller-pipe: untrusted-pipe\nsecret: yaml-secret\nrules: [MATCH,DIRECT]\n"
	// The sequence format is used below to retain a single MATCH rule.
	document := strings.ReplaceAll(yaml, "rules: [MATCH,DIRECT]", "rules: ['MATCH,DIRECT']")
	if err := os.WriteFile(C.Path.Config(), []byte(document), 0o600); err != nil {
		t.Fatal(err)
	}
	cfg, err := executor.Parse()
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Controller.ExternalController != "127.0.0.1:19871" || cfg.Controller.Secret != secret || cfg.Controller.ExternalControllerTLS != "" || cfg.Controller.ExternalControllerUnix != "" || cfg.Controller.ExternalControllerPipe != "" {
		t.Fatal("YAML overrode managed controller policy")
	}
	if err := executor.ApplyConfig(cfg, true); err != nil {
		t.Fatal(err)
	}
	handler := router(true, "wrong-secret", "/untrusted-doh", Cors{AllowOrigins: []string{"*"}})
	request := func(method, path, body string, authenticated bool) *httptest.ResponseRecorder {
		r := httptest.NewRequest(method, path, strings.NewReader(body))
		if authenticated {
			r.Header.Set("Authorization", "Bearer "+secret)
		}
		if body != "" {
			r.Header.Set("Content-Type", "application/json")
		}
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, r)
		return w
	}
	assertState := func(expected string) {
		t.Helper()
		w := request(http.MethodGet, "/desktop", "", true)
		var status struct {
			State string `json:"state"`
		}
		if err := json.Unmarshal(w.Body.Bytes(), &status); err != nil || w.Code != http.StatusOK || status.State != expected {
			t.Fatalf("expected desktop state %s, got %d %s", expected, w.Code, w.Body.String())
		}
	}
	assertState("running")
	for _, path := range []string{"/desktop", "/configs/file", "/upgrade/check"} {
		if w := request(http.MethodGet, path, "", false); w.Code != http.StatusUnauthorized {
			t.Fatalf("missing authentication accepted at %s: %d", path, w.Code)
		}
	}
	for _, origin := range []string{"http://localhost:3000", "https://untrusted.example"} {
		r := httptest.NewRequest(http.MethodGet, "/desktop", nil)
		r.Header.Set("Authorization", "Bearer "+secret)
		r.Header.Set("Origin", origin)
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, r)
		allowed := w.Header().Get("Access-Control-Allow-Origin")
		if origin == "http://localhost:3000" {
			if w.Code != http.StatusOK || allowed != origin {
				t.Fatalf("desktop development origin rejected: %d %q", w.Code, allowed)
			}
		} else if allowed != "" {
			t.Fatal("managed CORS allowed an untrusted origin")
		}
	}
	for _, path := range []string{"/restart", "/upgrade", "/upgrade/ui"} {
		w := request(http.MethodPost, path, "", true)
		if w.Code != http.StatusConflict || !strings.Contains(w.Body.String(), "desktop_managed") {
			t.Fatalf("desktop-owned mutation was not rejected at %s: %d %s", path, w.Code, w.Body.String())
		}
	}
	for _, path := range []string{"/debug/pprof/", "/ui/", "/untrusted-doh"} {
		if w := request(http.MethodGet, path, "", true); w.Code != http.StatusNotFound {
			t.Fatalf("unowned controller surface exposed at %s: %d", path, w.Code)
		}
	}
	if w := request(http.MethodGet, "/desktop", "", true); w.Code != http.StatusOK || strings.Contains(w.Body.String(), secret) || !strings.Contains(w.Body.String(), "rules_and_geodata") {
		t.Fatalf("invalid managed capability response: %d %s", w.Code, w.Body.String())
	}
	if w := request(http.MethodGet, "/configs/file", "", true); w.Code != http.StatusOK {
		t.Fatalf("managed configuration editing disabled: %d %s", w.Code, w.Body.String())
	}
	payload, _ := json.Marshal(map[string]string{"payload": document})
	if w := request(http.MethodPut, "/configs?force=true", string(payload), true); w.Code != http.StatusNoContent {
		t.Fatalf("managed force reload failed: %d %s", w.Code, w.Body.String())
	}
	if updater.DefaultCoreUpdater.AutoUpdate() || updater.CurrentUIUpdater().AutoUpdate() {
		t.Fatal("reload reenabled desktop-owned automatic updates")
	}
	if w := request(http.MethodGet, "/desktop", "", false); w.Code != http.StatusUnauthorized {
		t.Fatal("force reload lost authentication")
	}
	// Rejected parsing never begins configuration mutation and must preserve a
	// previously ready core. A real late TUN constructor failure must clear it.
	invalidPayload, _ := json.Marshal(map[string]string{"payload": "mixed-port: [unterminated\n"})
	if w := request(http.MethodPut, "/configs?force=true", string(invalidPayload), true); w.Code != http.StatusBadRequest {
		t.Fatalf("invalid reload was accepted: %d %s", w.Code, w.Body.String())
	}
	assertState("running")
	// DNS hijack address parsing occurs inside sing_tun.New, before it creates
	// a device. This exercises a real ApplyConfig failure on all CI platforms
	// without requiring TUN privileges or altering the machine's routes.
	failedTun := document + "tun:\n  enable: true\n  dns-hijack: ['invalid-address']\n"
	failedPayload, _ := json.Marshal(map[string]string{"payload": failedTun})
	if w := request(http.MethodPut, "/configs?force=true", string(failedPayload), true); w.Code != http.StatusInternalServerError || !strings.Contains(w.Body.String(), "dns-hijack") {
		t.Fatalf("late TUN apply failure was not returned: %d %s", w.Code, w.Body.String())
	}
	assertState("loading")
	if w := request(http.MethodPut, "/configs?force=true", string(payload), true); w.Code != http.StatusNoContent {
		t.Fatalf("valid recovery reload failed: %d %s", w.Code, w.Body.String())
	}
	assertState("running")
	if err := updater.DefaultCoreUpdater.UpdateContext(context.Background(), "must-not-run.exe", "release", true); !errors.Is(err, desktop.ErrManaged) {
		t.Fatalf("core installer bypassed managed policy: %v", err)
	}
	if err := updater.CurrentUIUpdater().DownloadUIContext(context.Background()); !errors.Is(err, desktop.ErrManaged) {
		t.Fatalf("UI installer bypassed managed policy: %v", err)
	}
	called := false
	if err := executor.ApplyUpdateAndRestart(func() error { called = true; return nil }, func() { called = true }); !errors.Is(err, desktop.ErrManaged) || called {
		t.Fatal("managed process replacement entered commit or restart")
	}
	if err := executor.Restart(func() { called = true }); !errors.Is(err, desktop.ErrManaged) || called {
		t.Fatal("managed restart entered process replacement")
	}
	if w := request(http.MethodPost, "/upgrade/check", "", true); w.Code != http.StatusAccepted {
		t.Fatalf("data-only unified update was rejected: %d %s", w.Code, w.Body.String())
	}
	snapshot := waitUpgradeTask(t, unifiedUpgrades, "completed")
	for _, item := range snapshot.Items {
		if (item.Kind == "kernel" || item.Kind == "ui") && (item.Status != "skipped" || item.Reason != "desktop_managed") {
			t.Fatalf("program update incorrectly participated: %+v", item)
		}
	}
	if _, err := os.Stat(filepath.Join(home, "ui")); !os.IsNotExist(err) {
		t.Fatal("bundled desktop unexpectedly bootstrapped a core-hosted UI")
	}
	outside := filepath.Join(t.TempDir(), "outside.yaml")
	if C.Path.IsSafePath(outside) {
		t.Fatal("legacy unsafe path environment bypassed managed policy")
	}
	if err := resource.NewFileVehicle(outside).Write([]byte("must not escape")); err == nil {
		t.Fatal("resource writer escaped managed home")
	}
	if _, err := os.Stat(outside); !os.IsNotExist(err) {
		t.Fatal("rejected resource write touched outside path")
	}
	if w := request(http.MethodPost, "/desktop/shutdown", "", false); w.Code != http.StatusUnauthorized || executor.ShutdownRequested() {
		t.Fatal("unauthenticated shutdown changed lifecycle")
	}
	if w := request(http.MethodPost, "/desktop/shutdown", "{}", true); w.Code != http.StatusBadRequest || executor.ShutdownRequested() {
		t.Fatal("invalid shutdown body changed lifecycle")
	}
	w := request(http.MethodPost, "/desktop/shutdown", "", true)
	if w.Code != http.StatusAccepted || !bytes.Equal(w.Body.Bytes(), []byte("{\"status\":\"stopping\"}\n")) || !w.Flushed || !executor.ShutdownRequested() {
		t.Fatalf("shutdown did not acknowledge and close admission: %d %s", w.Code, w.Body.String())
	}
	select {
	case <-desktop.ShutdownRequests():
	default:
		t.Fatal("shutdown did not wake daemon cleanup")
	}
	if err := executor.ApplyConfig(cfg, true); !errors.Is(err, executor.ErrShuttingDown) {
		t.Fatalf("configuration reopened after shutdown: %v", err)
	}
	assertState("stopping")
}
