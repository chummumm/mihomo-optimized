package route

import (
	"context"
	"encoding/json"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/metacubex/mihomo/component/desktop"

	"github.com/metacubex/http"
)

func TestDesktopManagedControllerLifecycle(t *testing.T) {
	for _, scenario := range []string{"ephemeral_reload", "occupied_port", "publication_failure"} {
		t.Run(scenario, func(t *testing.T) {
			executable, err := os.Executable()
			if err != nil {
				t.Fatal(err)
			}
			ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
			defer cancel()
			cmd := exec.CommandContext(ctx, executable, "-test.run=^TestDesktopManagedControllerProcess$", "-test.v")
			cmd.Env = append(os.Environ(), "MIHOMO_DESKTOP_CONTROLLER_TEST="+scenario)
			if output, err := cmd.CombinedOutput(); err != nil {
				t.Fatalf("controller process failed: %v\n%s", err, output)
			}
		})
	}
}

func TestDesktopManagedControllerProcess(t *testing.T) {
	scenario := os.Getenv("MIHOMO_DESKTOP_CONTROLLER_TEST")
	if scenario == "" {
		t.Skip("isolated managed controller helper")
	}
	home := t.TempDir()
	address := "127.0.0.1:0"
	if scenario == "occupied_port" {
		holder, err := net.Listen("tcp", address)
		if err != nil {
			t.Fatal(err)
		}
		defer holder.Close()
		address = holder.Addr().String()
	}
	const secret = "controller-test-secret-0123456789abcdef"
	if err := desktop.Enable(desktop.Options{
		Home: home, Controller: address, Secret: secret, LaunchNonce: strings.Repeat("a0", 32),
	}); err != nil {
		t.Fatal(err)
	}
	defer func() {
		if err := CloseManagedController(); err != nil {
			t.Errorf("controller cleanup: %v", err)
		}
	}()
	path := filepath.Join(desktop.Home(), desktop.ControllerRecordName)
	if scenario == "publication_failure" {
		if err := os.Mkdir(path, 0o700); err != nil {
			t.Fatal(err)
		}
	}
	desktop.SetReady(true)
	if desktop.Ready() {
		t.Fatal("resource readiness escaped before the controller bind")
	}
	err := ReCreateServer(&Config{})
	if scenario != "ephemeral_reload" {
		want := "desktop controller listen"
		if scenario == "publication_failure" {
			want = "desktop controller discovery"
		}
		if err == nil || !strings.Contains(err.Error(), want) {
			t.Fatalf("startup did not synchronously report %s: %v", want, err)
		}
		if managedController != nil || desktop.Ready() {
			t.Fatal("failed startup retained a managed controller")
		}
		return
	}
	if err != nil {
		t.Fatal(err)
	}
	before := managedController
	actual, _ := desktop.Controller()
	if actual == address || !desktop.Ready() {
		t.Fatal("ephemeral controller did not publish its bound endpoint")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var record desktop.ControllerRecord
	if err := json.Unmarshal(data, &record); err != nil {
		t.Fatal(err)
	}
	client := &http.Client{Timeout: 10 * time.Second}
	assertStatus := func() {
		t.Helper()
		request, err := http.NewRequest(http.MethodGet, "http://"+actual+"/desktop", nil)
		if err != nil {
			t.Fatal(err)
		}
		request.Header.Set("Authorization", "Bearer "+secret)
		response, err := client.Do(request)
		if err != nil {
			t.Fatal(err)
		}
		defer response.Body.Close()
		var status struct {
			PID          int    `json:"pid"`
			Controller   string `json:"controller"`
			CoreInstance string `json:"core_instance"`
			State        string `json:"state"`
		}
		if response.StatusCode != http.StatusOK || json.NewDecoder(response.Body).Decode(&status) != nil {
			t.Fatal("bound controller did not serve authenticated desktop status")
		}
		if status.PID != os.Getpid() || status.Controller != actual || status.CoreInstance != record.CoreInstance || status.State != "running" {
			t.Fatalf("desktop status disagreed with the bound process: %+v", status)
		}
	}
	assertStatus()
	for _, requested := range []string{"127.0.0.1:0", actual, "0.0.0.0:1"} {
		if err := ReCreateServer(&Config{Addr: requested, Secret: "untrusted"}); err != nil {
			t.Fatal(err)
		}
		if managedController != before {
			t.Fatal("configuration reload replaced the process-owned listener")
		}
		if reloaded, _ := desktop.Controller(); reloaded != actual {
			t.Fatal("configuration reload changed the controller endpoint")
		}
		assertStatus()
	}
	if err := CloseManagedController(); err != nil {
		t.Fatal(err)
	}
	if desktop.Ready() {
		t.Fatal("closed controller remained ready")
	}
	if _, err := os.Lstat(path); !os.IsNotExist(err) {
		t.Fatalf("closed controller kept its discovery record: %v", err)
	}
}
