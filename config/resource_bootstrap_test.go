package config

import (
	"context"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/mihomo/common/utils"
	"github.com/metacubex/mihomo/component/resource"
	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/transport/socks5"
)

func TestResourceBootstrapConfigBudget(t *testing.T) {
	for _, input := range []string{"", "resource-bootstrap: null", "resource-bootstrap: {}", "resource-bootstrap: {timeout: 2}"} {
		raw, err := UnmarshalRawConfig([]byte(input))
		if err != nil {
			t.Fatal(err)
		}
		scope, err := newResourceBootstrap(resource.WithBootstrapTimeout(context.Background(), 500*time.Millisecond), raw)
		if err != nil {
			t.Fatalf("%s: %v", input, err)
		}
		deadline, ok := scope.Context().Deadline()
		if !ok || time.Until(deadline) > 500*time.Millisecond || time.Until(deadline) < 300*time.Millisecond {
			t.Fatalf("supervisor limit ignored: %s", input)
		}
		if resource.BootstrapDialer(scope.Context()) != nil {
			t.Fatal("timeout-only config changed routing")
		}
		_ = scope.Close()
	}
	for _, input := range []string{"0", "-1", "null", "1.5", "\"2\"", "true", "9223372037", "99999999999999999999999999"} {
		if _, err := UnmarshalRawConfig([]byte("resource-bootstrap: {timeout: " + input + "}")); err == nil {
			t.Fatalf("accepted timeout %s", input)
		}
	}
}

func TestResourceBootstrapRejectsUnavailableAndCyclicDependenciesBeforeDownload(t *testing.T) {
	for _, tc := range []struct{ body, code string }{
		{"resource-bootstrap: {proxy: missing}", "proxy_unavailable"},
		{"resource-bootstrap: {proxy-provider: provider}", "missing_proxy"},
		{"resource-bootstrap: {proxy: group}\nproxy-groups: [{name: group, type: select, proxies: [DIRECT]}]", "dynamic_proxy"},
		{"resource-bootstrap: {proxy: a}\nproxies: [{name: a, type: socks5, server: 127.0.0.1, port: 1, dialer-proxy: b}, {name: b, type: socks5, server: 127.0.0.1, port: 1, dialer-proxy: a}]", "proxy_cycle"},
		{"resource-bootstrap: {proxy: a}\nproxies: [{name: a, type: socks5, server: 127.0.0.1, port: 1, dialer-proxy: group}]\nproxy-groups: [{name: group, type: select, proxies: [DIRECT]}]", "dynamic_proxy"},
		{"resource-bootstrap: {proxy: a, proxy-provider: remote}\nproxy-providers: {remote: {type: http, url: 'http://127.0.0.1:1/subscription'}}", "provider_cache_unavailable"},
	} {
		raw, err := UnmarshalRawConfig([]byte(tc.body))
		if err != nil {
			t.Fatal(err)
		}
		if scope, err := newResourceBootstrap(context.Background(), raw); err == nil {
			_ = scope.Close()
			t.Fatalf("accepted invalid bootstrap: %s", tc.body)
		} else if !strings.Contains(err.Error(), "code="+tc.code) {
			t.Fatalf("want %s: %v", tc.code, err)
		}
	}
}

// The real SOCKS fixture allows a resource with no local DNS record to be
// reached only through the selected temporary adapter (and an optional chain).
func resourceBootstrapSOCKS(t *testing.T, target func(string) string) (map[string]any, *atomic.Int32) {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	var calls atomic.Int32
	var active sync.WaitGroup
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			conn, err := listener.Accept()
			if err != nil {
				return
			}
			active.Add(1)
			go func() {
				defer active.Done()
				defer conn.Close()
				_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
				address, command, _, err := socks5.ServerHandshake(conn, nil)
				if err != nil || command != socks5.CmdConnect {
					return
				}
				calls.Add(1)
				remote, err := net.DialTimeout("tcp", target(address.String()), time.Second)
				if err != nil {
					return
				}
				defer remote.Close()
				copied := make(chan struct{})
				go func() { _, _ = io.Copy(remote, conn); _ = remote.Close(); close(copied) }()
				_, _ = io.Copy(conn, remote)
				_ = conn.Close()
				<-copied
			}()
		}
	}()
	t.Cleanup(func() { _ = listener.Close(); <-done; active.Wait() })
	host, port, _ := net.SplitHostPort(listener.Addr().String())
	number, _ := strconv.Atoi(port)
	return map[string]any{"type": "socks5", "server": host, "port": number}, &calls
}

func TestResourceBootstrapStaticChainAndClose(t *testing.T) {
	origin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("through chain")) }))
	defer origin.Close()
	last, lastCalls := resourceBootstrapSOCKS(t, func(string) string { return strings.TrimPrefix(origin.URL, "http://") })
	first, firstCalls := resourceBootstrapSOCKS(t, func(address string) string { return address })
	first["name"], last["name"], last["dialer-proxy"] = "first", "last", "first"
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{first, last}
	raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "last", Timeout: 2}
	scope, err := newResourceBootstrap(context.Background(), raw)
	if err != nil {
		t.Fatal(err)
	}
	defer scope.Close()
	vehicle := resource.NewHTTPVehicle("http://unresolvable.invalid/rules", "unused", "", nil, time.Second, 1024)
	body, _, err := vehicle.Read(scope.Context(), utils.HashType{})
	if err != nil || string(body) != "through chain" {
		t.Fatalf("chain download = %q %v", body, err)
	}
	if firstCalls.Load() != 1 || lastCalls.Load() != 1 {
		t.Fatalf("chain bypassed: %d %d", firstCalls.Load(), lastCalls.Load())
	}
	_ = scope.Close()
	if _, err := resource.BootstrapDialer(scope.Context()).DialContext(context.Background(), "tcp", "127.0.0.1:1"); err == nil {
		t.Fatal("closed temporary route admitted new traffic")
	}
}

func TestResourceBootstrapFailedSelectedRouteNeverFallsBack(t *testing.T) {
	var hits atomic.Int32
	origin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { hits.Add(1); _, _ = w.Write([]byte("must not reach")) }))
	defer origin.Close()
	unused, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	_, port, _ := net.SplitHostPort(unused.Addr().String())
	_ = unused.Close()
	raw := DefaultRawConfig()
	raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "offline", Timeout: 1}
	raw.Proxy = []map[string]any{{"name": "offline", "type": "socks5", "server": "127.0.0.1", "port": port}}
	scope, err := newResourceBootstrap(context.Background(), raw)
	if err != nil {
		t.Fatal(err)
	}
	defer scope.Close()
	vehicle := resource.NewHTTPVehicle(origin.URL, "unused", "", nil, time.Second, 1024)
	if _, _, err := vehicle.Read(scope.Context(), utils.HashType{}); err == nil {
		t.Fatal("selected route failure was hidden")
	}
	if hits.Load() != 0 {
		t.Fatal("failed bootstrap route fell back to DIRECT")
	}
}

func TestResourceBootstrapProviderUsesExistingFilteredCache(t *testing.T) {
	previousHome := C.Path.HomeDir()
	C.SetHomeDir(t.TempDir())
	t.Cleanup(func() { C.SetHomeDir(previousHome) })
	path := filepath.Join(C.Path.HomeDir(), "cached.yaml")
	data := []byte("proxies:\n  - {name: keep, type: socks5, server: 127.0.0.1, port: 1080}\n  - {name: exclude, type: socks5, server: 127.0.0.1, port: 1081}\n")
	if err := os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{{"name": "chosen-keep", "type": "reject"}}
	raw.ProxyProvider = map[string]map[string]any{"chosen": {"type": "http", "url": "http://127.0.0.1:1/unreachable", "path": path, "filter": "^keep$", "override": map[string]any{"additional-prefix": "chosen-"}}}
	raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "chosen-keep", ProxyProvider: "chosen", Timeout: 2}
	scope, err := newResourceBootstrap(context.Background(), raw)
	if err != nil {
		t.Fatal(err)
	}
	defer scope.Close()
	temporary := resource.BootstrapDialer(scope.Context()).(*bootstrapOutbound)
	mapping, err := temporary.mapping("chosen-keep", true)
	if err != nil || mapping["type"] != "socks5" {
		t.Fatalf("provider selection ignored override or picked same-name static node: %#v %v", mapping, err)
	}
	if _, err := temporary.mapping("exclude", true); err == nil {
		t.Fatal("provider filter was bypassed")
	}
	after, _ := os.ReadFile(path)
	if string(after) != string(data) {
		t.Fatal("bootstrap cache inspection rewrote existing file")
	}
}

func TestResourceBootstrapGenericAdaptersAndUDP(t *testing.T) {
	for _, mapping := range []map[string]any{
		{"name": "node", "type": "anytls", "server": "127.0.0.1", "port": 443, "password": "fixture"},
		{"name": "node", "type": "hysteria2", "server": "127.0.0.1", "port": 443, "password": "fixture"},
		{"name": "node", "type": "tuic", "server": "127.0.0.1", "port": 443, "uuid": "550e8400-e29b-41d4-a716-446655440000", "password": "fixture"},
	} {
		t.Run(mapping["type"].(string), func(t *testing.T) {
			raw := DefaultRawConfig()
			raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "node", Timeout: 2}
			raw.Proxy = []map[string]any{mapping}
			scope, err := newResourceBootstrap(context.Background(), raw)
			if err != nil {
				t.Fatal(err)
			}
			defer scope.Close()
			done := make(chan error, 1)
			go func() {
				_, err := resource.BootstrapDialer(scope.Context()).(*bootstrapOutbound).outbound()
				done <- err
			}()
			select {
			case err := <-done:
				if err != nil {
					t.Fatal(err)
				}
			case <-time.After(time.Second):
				t.Fatal("generic adapter construction deadlocked")
			}
		})
	}
	server, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer server.Close()
	raw := DefaultRawConfig()
	raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "DIRECT", Timeout: 2}
	scope, err := newResourceBootstrap(context.Background(), raw)
	if err != nil {
		t.Fatal(err)
	}
	defer scope.Close()
	destination := server.LocalAddr().(*net.UDPAddr).AddrPort()
	packet, err := resource.BootstrapDialer(scope.Context()).ListenPacket(scope.Context(), "udp", "", netip.AddrPortFrom(destination.Addr(), destination.Port()))
	if err != nil {
		t.Fatal(err)
	}
	defer packet.Close()
	if _, err := packet.WriteTo([]byte("UDP works"), server.LocalAddr()); err != nil {
		t.Fatal(err)
	}
	_ = server.SetReadDeadline(time.Now().Add(time.Second))
	buf := make([]byte, 32)
	if n, _, err := server.ReadFrom(buf); err != nil || string(buf[:n]) != "UDP works" {
		t.Fatalf("bootstrap UDP = %q %v", buf[:n], err)
	}
}
