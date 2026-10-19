package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
	"time"
)

// Exercise the actual daemon entry point and process exit, in addition to the
// in-process router contracts. This also runs on the native Windows CI job.
func TestDesktopManagedDaemon(t *testing.T) {
	if os.Getenv("MIHOMO_DESKTOP_DAEMON_CHILD") == "1" {
		os.Args = []string{os.Args[0]} // bootstrap through the documented environment
		main()
		return
	}
	home := t.TempDir()
	// Windows runners may use an 8.3 TMPDIR, and macOS commonly exposes /var
	// through /private/var. Exercise an equivalent parent alias on runners
	// that support symlinks so Linux also catches bootstrap path mismatches.
	alias := filepath.Join(t.TempDir(), "temporary-root")
	if err := os.Symlink(filepath.Dir(home), alias); err == nil {
		home = filepath.Join(alias, filepath.Base(home))
	}
	path := filepath.Join(home, "config.yaml")
	if err := os.WriteFile(path, []byte("mixed-port: 0\nmode: direct\ncore-auto-update: true\nexternal-ui-auto-update: true\nrules: ['MATCH,DIRECT']\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	address := listener.Addr().String()
	listener.Close()
	var entropy [32]byte
	if _, err := rand.Read(entropy[:]); err != nil {
		t.Fatal(err)
	}
	secret := base64.RawURLEncoding.EncodeToString(entropy[:])
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, executable, "-test.run=^TestDesktopManagedDaemon$")
	cmd.Env = append(os.Environ(), "MIHOMO_DESKTOP_DAEMON_CHILD=1", "CLASH_DESKTOP_MANAGED=true",
		"CLASH_HOME_DIR="+home, "CLASH_CONFIG_FILE="+path,
		"CLASH_OVERRIDE_EXTERNAL_CONTROLLER="+address, "CLASH_OVERRIDE_SECRET="+secret,
		"CLASH_CONFIG_STRING=", "CLASH_POST_UP=", "CLASH_POST_DOWN=")
	var output bytes.Buffer
	cmd.Stdout, cmd.Stderr = &output, &output
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	finished := false
	t.Cleanup(func() {
		if !finished {
			_ = cmd.Process.Kill()
			<-done
		}
	})
	client := &http.Client{Timeout: time.Second, Transport: &http.Transport{Proxy: nil}}
	defer client.CloseIdleConnections()
	request := func(method, endpoint string) (*http.Response, error) {
		r, err := http.NewRequestWithContext(ctx, method, "http://"+address+endpoint, nil)
		if err != nil {
			return nil, err
		}
		r.Header.Set("Authorization", "Bearer "+secret)
		return client.Do(r)
	}
	tick := time.NewTicker(20 * time.Millisecond)
	defer tick.Stop()
	ready := false
	for !ready {
		select {
		case err := <-done:
			finished = true
			t.Fatalf("daemon exited before ready: %v\n%s", err, output.String())
		case <-ctx.Done():
			t.Fatal("managed daemon did not become ready")
		case <-tick.C:
			response, err := request(http.MethodGet, "/desktop")
			if err != nil {
				continue
			}
			var status struct {
				Managed bool   `json:"managed"`
				PID     int    `json:"pid"`
				State   string `json:"state"`
			}
			err = json.NewDecoder(response.Body).Decode(&status)
			response.Body.Close()
			ready = response.StatusCode == http.StatusOK && err == nil && status.Managed && status.PID == cmd.Process.Pid && status.State == "running"
		}
	}
	response, err := request(http.MethodPost, "/desktop/shutdown")
	if err != nil {
		t.Fatal(err)
	}
	body, err := io.ReadAll(response.Body)
	response.Body.Close()
	if err != nil || response.StatusCode != http.StatusAccepted || string(body) != "{\"status\":\"stopping\"}\n" {
		t.Fatalf("shutdown acknowledgement was incomplete: %d %q %v", response.StatusCode, body, err)
	}
	select {
	case err := <-done:
		finished = true
		if err != nil {
			t.Fatalf("managed daemon cleanup failed: %v\n%s", err, output.String())
		}
	case <-ctx.Done():
		t.Fatal("acknowledged shutdown did not exit the actual daemon")
	}
	if bytes.Contains(output.Bytes(), []byte(secret)) {
		t.Fatal("daemon leaked its bootstrap secret in output")
	}
	if _, err := os.Stat(filepath.Join(home, "ui")); !os.IsNotExist(err) {
		t.Fatal("managed daemon downloaded a core-hosted UI")
	}
}
