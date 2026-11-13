package desktop

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func controllerPolicyForTest(t *testing.T, nonce string) *policy {
	t.Helper()
	home := t.TempDir()
	previous := current.Swap(nil)
	t.Cleanup(func() {
		if err := WithdrawController(); err != nil {
			t.Errorf("withdraw test controller: %v", err)
		}
		current.Store(previous)
	})
	options := Options{
		Home:        home,
		Controller:  "127.0.0.1:0",
		Secret:      strings.Repeat("secret", 8),
		LaunchNonce: nonce,
	}
	if nonce == "" {
		options.Controller = "127.0.0.1:19871"
	}
	if err := Enable(options); err != nil {
		t.Fatal(err)
	}
	return current.Load()
}

func TestDesktopControllerDiscoveryPolicy(t *testing.T) {
	base := Options{
		Home:        t.TempDir(),
		Controller:  "127.0.0.1:0",
		Secret:      strings.Repeat("secret", 8),
		LaunchNonce: strings.Repeat("a0", 32),
	}
	if _, err := validateOptions(base); err != nil {
		t.Fatal(err)
	}
	for _, nonce := range []string{"", "a", strings.Repeat("A", 64), strings.Repeat("z", 64), strings.Repeat("a", 63), strings.Repeat("a", 65)} {
		options := base
		options.LaunchNonce = nonce
		if _, err := validateOptions(options); err == nil {
			t.Errorf("invalid discovery nonce of length %d was accepted", len(nonce))
		}
	}
	options := base
	options.Controller = "[::1]:0"
	if _, err := validateOptions(options); err == nil {
		t.Fatal("discovery accepted an endpoint outside its IPv4 contract")
	}
	options.Controller = "127.0.0.1:19871"
	options.LaunchNonce = ""
	if _, err := validateOptions(options); err != nil {
		t.Fatalf("legacy fixed-port managed launch rejected: %v", err)
	}
}

func TestDesktopControllerPublicationAndReadiness(t *testing.T) {
	p := controllerPolicyForTest(t, strings.Repeat("a0", 32))
	SetReady(true)
	if Ready() {
		t.Fatal("resources alone made the process ready before a controller was bound")
	}
	path := filepath.Join(p.Home, ControllerRecordName)
	stale := ControllerRecord{ProtocolVersion: 1, PID: 999, Controller: "127.0.0.1:1", CoreInstance: "stale", LaunchNonce: strings.Repeat("b0", 32)}
	if err := writeControllerRecord(p.Home, stale); err != nil {
		t.Fatal(err)
	}
	if err := PublishController("127.0.0.1:19872", "instance-1"); err != nil {
		t.Fatal(err)
	}
	if !Ready() {
		t.Fatal("bound controller and applied resources did not publish readiness")
	}
	address, secret := Controller()
	if address != "127.0.0.1:19872" || secret != p.Secret {
		t.Fatal("controller did not expose the actual bound endpoint")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil {
		t.Fatal(err)
	}
	if len(fields) != 5 || len(data) > ControllerRecordMaxBytes || bytes.Contains(data, []byte(p.Secret)) {
		t.Fatal("record violated the bounded five-field credential-free contract")
	}
	var record ControllerRecord
	if err := json.Unmarshal(data, &record); err != nil {
		t.Fatal(err)
	}
	if record.ProtocolVersion != 1 || record.PID != os.Getpid() || record.Controller != address || record.CoreInstance != "instance-1" || record.LaunchNonce != p.LaunchNonce {
		t.Fatal("record did not identify the current process, launch and endpoint")
	}
	if err := PublishController(address, "instance-1"); err != nil {
		t.Fatal(err)
	}
	again, err := os.ReadFile(path)
	if err != nil || !bytes.Equal(data, again) {
		t.Fatal("idempotent publication changed the discovery record")
	}
	if err := PublishController("127.0.0.1:19873", "instance-1"); err == nil {
		t.Fatal("live controller ownership was transferred to another endpoint")
	}
	if err := WithdrawController(); err != nil {
		t.Fatal(err)
	}
	if Ready() {
		t.Fatal("withdrawn controller remained ready")
	}
	if _, err := os.Lstat(path); !os.IsNotExist(err) {
		t.Fatalf("owned discovery record was not removed: %v", err)
	}
}

func TestDesktopControllerPublicationFailureDoesNotBecomeReady(t *testing.T) {
	p := controllerPolicyForTest(t, strings.Repeat("c0", 32))
	path := filepath.Join(p.Home, ControllerRecordName)
	if err := os.Mkdir(path, 0o700); err != nil {
		t.Fatal(err)
	}
	SetReady(true)
	if err := PublishController("127.0.0.1:19872", "instance"); err == nil {
		t.Fatal("discovery publication unexpectedly replaced a directory")
	}
	if Ready() || p.controller.Load() != nil {
		t.Fatal("failed publication made the controller available")
	}
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	for _, address := range []string{"0.0.0.0:19872", "[::1]:19872", "127.0.0.1:0"} {
		if err := PublishController(address, "instance"); err == nil {
			t.Errorf("publication accepted unowned address %s", address)
		}
	}
}

func TestDesktopControllerExitPreservesAnotherLaunchRecord(t *testing.T) {
	p := controllerPolicyForTest(t, strings.Repeat("d0", 32))
	if err := PublishController("127.0.0.1:19872", "old-instance"); err != nil {
		t.Fatal(err)
	}
	replacement := ControllerRecord{
		ProtocolVersion: 1, PID: os.Getpid() + 1,
		Controller: "127.0.0.1:19873", CoreInstance: "new-instance",
		LaunchNonce: strings.Repeat("e0", 32),
	}
	if err := writeControllerRecord(p.Home, replacement); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(p.Home, ControllerRecordName)
	before, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if err := WithdrawController(); err != nil {
		t.Fatal(err)
	}
	after, err := os.ReadFile(path)
	if err != nil || !bytes.Equal(before, after) {
		t.Fatal("old process exit removed another launch's discovery record")
	}
}

func TestDesktopControllerLegacyFixedPortDoesNotWriteRecord(t *testing.T) {
	p := controllerPolicyForTest(t, "")
	SetReady(true)
	if err := PublishController("127.0.0.1:19872", "instance"); err == nil {
		t.Fatal("fixed policy accepted a different port")
	}
	if err := PublishController("127.0.0.1:19871", "instance"); err != nil {
		t.Fatal(err)
	}
	if !Ready() {
		t.Fatal("legacy fixed-port controller did not become ready")
	}
	if _, err := os.Lstat(filepath.Join(p.Home, ControllerRecordName)); !os.IsNotExist(err) {
		t.Fatal("legacy launch wrote a discovery record without a launch nonce")
	}
}
