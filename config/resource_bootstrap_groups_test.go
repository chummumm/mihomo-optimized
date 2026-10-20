package config

import (
	"context"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	stdlog "log"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/mihomo/common/utils"
	"github.com/metacubex/mihomo/component/ca"
	"github.com/metacubex/mihomo/component/resource"
	C "github.com/metacubex/mihomo/constant"
)

func bootstrapGroupFixture(t *testing.T, handler http.HandlerFunc) map[string]any {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	node, _ := resourceBootstrapSOCKS(t, func(string) string { return strings.TrimPrefix(server.URL, "http://") })
	return node
}

func bootstrapGroupScope(t *testing.T, raw *RawConfig) *resource.Bootstrap {
	t.Helper()
	if raw.ResourceBootstrap == nil {
		raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "group", Timeout: 3}
	}
	scope, err := newResourceBootstrap(context.Background(), raw)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = scope.Close() })
	return scope
}

func bootstrapGroupRead(scope *resource.Bootstrap, path, proxy string) ([]byte, error) {
	v := resource.NewHTTPVehicle("http://resource.invalid"+path, "unused", proxy, nil, time.Second, 1024)
	body, _, err := v.Read(scope.Context(), utils.HashType{})
	return body, err
}

func TestResourceBootstrapGroupRequestFailureAndStickySuccess(t *testing.T) {
	var badGET, goodGET atomic.Int32
	bad := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			w.WriteHeader(204)
			return
		}
		badGET.Add(1)
		w.WriteHeader(503)
	})
	good := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			w.WriteHeader(204)
			return
		}
		goodGET.Add(1)
		_, _ = w.Write([]byte("valid rules"))
	})
	bad["name"], good["name"] = "bad", "good"
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{bad, good}
	raw.ProxyGroup = []map[string]any{{"name": "group", "type": "fallback", "proxies": []string{"bad", "good"}, "url": "http://probe.invalid/204"}}
	scope := bootstrapGroupScope(t, raw)
	for i := 0; i < 2; i++ {
		body, err := bootstrapGroupRead(scope, "/rules", "")
		if err != nil || string(body) != "valid rules" {
			t.Fatalf("GET %q: %v", body, err)
		}
	}
	if badGET.Load() != 1 || goodGET.Load() != 2 {
		t.Fatalf("not request-level fallback/sticky: bad=%d good=%d", badGET.Load(), goodGET.Load())
	}
}

func TestResourceBootstrapNestedSelectKeepsChosenSubtree(t *testing.T) {
	var outside atomic.Int32
	bad := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			w.WriteHeader(204)
		} else {
			w.WriteHeader(503)
		}
	})
	good := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			w.WriteHeader(204)
		} else {
			_, _ = w.Write([]byte("chosen subtree"))
		}
	})
	other := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) { outside.Add(1); _, _ = w.Write([]byte("wrong subtree")) })
	bad["name"], good["name"], other["name"] = "bad", "good", "other"
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{bad, good, other}
	raw.ProxyGroup = []map[string]any{
		{"name": "inside", "type": "fallback", "proxies": []string{"bad", "good"}, "url": "http://probe.invalid/204"},
		{"name": "group", "type": "select", "proxies": []string{"other", "inside"}, "default-selected": "inside"},
	}
	scope := bootstrapGroupScope(t, raw)
	body, err := bootstrapGroupRead(scope, "/rules", "")
	if err != nil || string(body) != "chosen subtree" || outside.Load() != 0 {
		t.Fatalf("selector escaped: %q %v outside=%d", body, err, outside.Load())
	}
	// Choosing one failed leaf never silently moves to a different select member.
	raw.ProxyGroup[1]["default-selected"] = "bad"
	raw.ProxyGroup[1]["proxies"] = []string{"bad", "other"}
	next := bootstrapGroupScope(t, raw)
	if _, err := bootstrapGroupRead(next, "/rules", ""); err == nil || outside.Load() != 0 {
		t.Fatalf("select failed open: %v outside=%d", err, outside.Load())
	}
}

func TestResourceBootstrapGroupFailedProbeStillTriesResource(t *testing.T) {
	node := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			w.WriteHeader(503)
			return
		}
		_, _ = w.Write([]byte("resource works"))
	})
	node["name"] = "node"
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{node}
	raw.ProxyGroup = []map[string]any{{"name": "group", "type": "url-test", "proxies": []string{"node"}, "url": "http://probe.invalid/fails", "expected-status": "204"}}
	scope := bootstrapGroupScope(t, raw)
	if body, err := bootstrapGroupRead(scope, "/rules", ""); err != nil || string(body) != "resource works" {
		t.Fatalf("test endpoint became dependency: %q %v", body, err)
	}
}

func TestResourceBootstrapSameNameProvidersAndConcurrentExclusions(t *testing.T) {
	makeNode := func(serve string) map[string]any {
		node := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
			if r.Method == http.MethodHead {
				w.WriteHeader(204)
				return
			}
			if r.URL.Path != serve {
				w.WriteHeader(503)
				return
			}
			_, _ = w.Write([]byte(serve))
		})
		node["name"] = "same"
		return node
	}
	raw := DefaultRawConfig()
	raw.ProxyProvider = map[string]map[string]any{
		"first":  {"type": "inline", "payload": []map[string]any{makeNode("/a")}},
		"second": {"type": "inline", "payload": []map[string]any{makeNode("/b")}},
	}
	raw.ProxyGroup = []map[string]any{{"name": "group", "type": "fallback", "use": []string{"first", "second"}, "url": "http://probe.invalid/204"}}
	scope := bootstrapGroupScope(t, raw)
	var wg sync.WaitGroup
	failures := make(chan error, 8)
	for i := 0; i < 8; i++ {
		path := []string{"/a", "/b"}[i%2]
		wg.Add(1)
		go func() {
			defer wg.Done()
			body, err := bootstrapGroupRead(scope, path, "")
			if err != nil || string(body) != path {
				failures <- fmt.Errorf("%s => %q %v", path, body, err)
			}
		}()
	}
	wg.Wait()
	close(failures)
	for err := range failures {
		t.Error(err)
	}
}

func TestResourceBootstrapExplicitProviderRouteWithoutDefault(t *testing.T) {
	node := bootstrapGroupFixture(t, func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("explicit private route")) })
	node["name"] = "node"
	raw := DefaultRawConfig()
	raw.ResourceBootstrap = &ResourceBootstrap{Timeout: 2}
	raw.Proxy = []map[string]any{node}
	raw.ProxyGroup = []map[string]any{{"name": "explicit", "type": "select", "proxies": []string{"node"}}}
	scope := bootstrapGroupScope(t, raw)
	if resource.BootstrapDialer(scope.Context()) != nil {
		t.Fatal("unspecified default changed routing")
	}
	body, err := bootstrapGroupRead(scope, "/rules", "explicit")
	if err != nil || string(body) != "explicit private route" {
		t.Fatalf("explicit override bypassed local graph: %q %v", body, err)
	}
	if _, err := bootstrapGroupRead(scope, "/rules", "missing"); err == nil {
		t.Fatal("unknown override did not fail closed")
	}
}

func TestResourceBootstrapMissingProviderAndEmptyFallback(t *testing.T) {
	home := C.Path.HomeDir()
	C.SetHomeDir(t.TempDir())
	defer C.SetHomeDir(home)
	node := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			w.WriteHeader(204)
			return
		}
		_, _ = w.Write([]byte("static"))
	})
	node["name"] = "static"
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{node}
	raw.ProxyProvider = map[string]map[string]any{"not-yet": {"type": "http", "url": "http://127.0.0.1:1/never", "path": filepath.Join(C.Path.HomeDir(), "missing.yaml")}}
	raw.ProxyGroup = []map[string]any{{"name": "group", "type": "fallback", "proxies": []string{"static"}, "use": []string{"not-yet"}, "url": "http://probe.invalid/204"}}
	scope := bootstrapGroupScope(t, raw)
	if body, err := bootstrapGroupRead(scope, "/rules", ""); err != nil || string(body) != "static" {
		t.Fatalf("empty optional cache blocked static: %q %v", body, err)
	}
	delete(raw.ProxyGroup[0], "proxies")
	empty := bootstrapGroupScope(t, raw)
	if _, err := bootstrapGroupRead(empty, "/rules", ""); err == nil || !strings.Contains(err.Error(), "no_usable_proxy") {
		t.Fatalf("empty group did not fail closed: %v", err)
	}
	if _, err := os.Stat(filepath.Join(C.Path.HomeDir(), "missing.yaml")); !os.IsNotExist(err) {
		t.Fatal("bootstrap downloaded/wrote the subscription")
	}
}

func TestResourceBootstrapGroupProbeConcurrencyCancellationAndWarmCache(t *testing.T) {
	var active, maxActive, started atomic.Int32
	origin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		count := active.Add(1)
		defer active.Add(-1)
		started.Add(1)
		for {
			old := maxActive.Load()
			if count <= old || maxActive.CompareAndSwap(old, count) {
				break
			}
		}
		<-r.Context().Done()
	}))
	defer origin.Close()
	raw := DefaultRawConfig()
	var names []string
	for i := 0; i < 25; i++ {
		node, _ := resourceBootstrapSOCKS(t, func(string) string { return strings.TrimPrefix(origin.URL, "http://") })
		name := fmt.Sprintf("node%d", i)
		node["name"] = name
		names = append(names, name)
		raw.Proxy = append(raw.Proxy, node)
	}
	raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "group", Timeout: 3}
	raw.ProxyGroup = []map[string]any{{"name": "group", "type": "url-test", "proxies": names, "url": "http://probe.invalid/stall", "timeout": 1000}}
	ctx := resource.WithBootstrapTimeout(context.Background(), 150*time.Millisecond)
	scope, err := newResourceBootstrap(ctx, raw)
	if err != nil {
		t.Fatal(err)
	}
	defer scope.Close()
	if started.Load() != 0 {
		t.Fatal("warm configuration performed network probes")
	}
	start := time.Now()
	_, err = bootstrapGroupRead(scope, "/rules", "")
	if !errors.Is(err, context.DeadlineExceeded) || time.Since(start) > time.Second {
		t.Fatalf("probe deadline not shared: %v %s", err, time.Since(start))
	}
	if maxActive.Load() > 10 || maxActive.Load() < 2 {
		t.Fatalf("probe concurrency=%d", maxActive.Load())
	}
	_ = scope.Close()
	deadline := time.Now().Add(time.Second)
	for active.Load() != 0 && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	if active.Load() != 0 {
		t.Fatalf("probe workers remained after scope close: %d", active.Load())
	}
}

func TestResourceBootstrapHungProbeReservesActualDownloadBudget(t *testing.T) {
	var heads, gets atomic.Int32
	node := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			heads.Add(1)
			<-r.Context().Done()
			return
		}
		gets.Add(1)
		_, _ = w.Write([]byte("actual resource"))
	})
	node["name"] = "node"
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{node}
	raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "group", Timeout: 1}
	raw.ProxyGroup = []map[string]any{{"name": "group", "type": "url-test", "proxies": []string{"node"}, "url": "http://probe.invalid/hangs", "timeout": 5000}}
	scope := bootstrapGroupScope(t, raw)
	start := time.Now()
	for i := 0; i < 2; i++ {
		body, err := bootstrapGroupRead(scope, "/rules", "")
		if err != nil || string(body) != "actual resource" {
			t.Fatalf("probe blocked GET: %q %v", body, err)
		}
	}
	if heads.Load() != 1 || gets.Load() != 2 || time.Since(start) > 800*time.Millisecond {
		t.Fatalf("probe budget/repetition: HEAD=%d GET=%d elapsed=%s", heads.Load(), gets.Load(), time.Since(start))
	}
}

func TestResourceBootstrapSharedDAGValidationAndExhaustionAreBounded(t *testing.T) {
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{{"name": "leaf", "type": "reject"}}
	previous := "leaf"
	for i := 0; i < 30; i++ {
		name := fmt.Sprintf("shared%d", i)
		raw.ProxyGroup = append(raw.ProxyGroup, map[string]any{"name": name, "type": "fallback", "proxies": []string{previous, previous}, "url": "http://probe.invalid/204"})
		previous = name
	}
	raw.ResourceBootstrap = &ResourceBootstrap{Proxy: previous, Timeout: 1}
	start := time.Now()
	scope := bootstrapGroupScope(t, raw)
	owner := resource.BootstrapDialer(scope.Context()).(*bootstrapOutbound)
	request, err := owner.request(scope.Context(), previous, false, "http://resource.invalid/rules")
	if err != nil {
		t.Fatal(err)
	}
	// This is the second-attempt state of a real failed GET. Skip unrelated HEAD
	// I/O here to isolate the graph's finite validation/availability contract.
	request.prepared = true
	if _, err = request.Next(scope.Context()); err != nil {
		t.Fatal(err)
	}
	request.Failed()
	if _, err = request.Next(scope.Context()); err == nil || !strings.Contains(err.Error(), "no_usable_proxy") {
		t.Fatalf("exhaustion=%v", err)
	}
	if time.Since(start) > time.Second {
		t.Fatal("shared DAG revisited every path")
	}
	canceled, cancel := context.WithCancel(scope.Context())
	cancel()
	if _, err = request.Next(canceled); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation=%v", err)
	}
}

func TestResourceBootstrapTruncatedBodyRetriesNextCandidate(t *testing.T) {
	var badGET, goodGET atomic.Int32
	bad := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			w.WriteHeader(204)
			return
		}
		badGET.Add(1)
		w.Header().Set("Content-Length", "100")
		_, _ = w.Write([]byte("truncated"))
	})
	bad["name"] = "bad"
	good := bootstrapGroupFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodHead {
			w.WriteHeader(204)
			return
		}
		goodGET.Add(1)
		_, _ = w.Write([]byte("whole body"))
	})
	good["name"] = "good"
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{bad, good}
	raw.ProxyGroup = []map[string]any{{"name": "group", "type": "fallback", "proxies": []string{"bad", "good"}, "url": "http://probe.invalid/204"}}
	scope := bootstrapGroupScope(t, raw)
	body, err := bootstrapGroupRead(scope, "/rules", "")
	if err != nil || string(body) != "whole body" || badGET.Load() != 1 || goodGET.Load() != 1 {
		t.Fatalf("body retry: %q %v bad=%d good=%d", body, err, badGET.Load(), goodGET.Load())
	}
}

func TestResourceBootstrapTLSFailureRetriesWithoutWeakeningVerification(t *testing.T) {
	var gets atomic.Int32
	secure := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { gets.Add(1); _, _ = w.Write([]byte("verified TLS")) }))
	defer secure.Close()
	secure.Config.ErrorLog = stdlog.New(io.Discard, "", 0)
	plain := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(204) }))
	defer plain.Close()
	mapping := func(actual string) (map[string]any, *atomic.Int32) {
		return resourceBootstrapSOCKS(t, func(address string) string {
			if strings.HasPrefix(address, "probe.invalid:") {
				return strings.TrimPrefix(plain.URL, "http://")
			}
			return actual
		})
	}
	bad, badCalls := mapping(strings.TrimPrefix(plain.URL, "http://"))
	good, _ := mapping(strings.TrimPrefix(secure.URL, "https://"))
	bad["name"], good["name"] = "plain-instead-of-tls", "verified"
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{bad, good}
	raw.ProxyGroup = []map[string]any{{"name": "group", "type": "fallback", "proxies": []string{"plain-instead-of-tls", "verified"}, "url": "http://probe.invalid/204"}}
	cert := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: secure.Certificate().Raw})
	if err := ca.AddCertificate(string(cert)); err != nil {
		t.Fatal(err)
	}
	defer ca.ResetCertificate()
	scope := bootstrapGroupScope(t, raw)
	vehicle := resource.NewHTTPVehicle(secure.URL+"/rules", "unused", "", nil, time.Second, 1024)
	body, _, err := vehicle.Read(scope.Context(), utils.HashType{})
	if err != nil || string(body) != "verified TLS" || gets.Load() != 1 || badCalls.Load() < 2 {
		t.Fatalf("TLS retry: %q %v gets=%d badCalls=%d", body, err, gets.Load(), badCalls.Load())
	}
	_ = scope.Close()
	ca.ResetCertificate()
	raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "verified", Timeout: 2}
	untrusted := bootstrapGroupScope(t, raw)
	if _, _, err := vehicle.Read(untrusted.Context(), utils.HashType{}); err == nil || gets.Load() != 1 {
		t.Fatalf("certificate verification was bypassed: %v gets=%d", err, gets.Load())
	}
}

func TestResourceBootstrapSharedDAGMemoRetainsCompleteDepthLimit(t *testing.T) {
	raw := DefaultRawConfig()
	raw.Proxy = []map[string]any{{"name": "leaf", "type": "reject"}}
	chain := func(prefix, previous string) string {
		for i := 0; i < 40; i++ {
			name := fmt.Sprintf("%s%d", prefix, i)
			raw.ProxyGroup = append(raw.ProxyGroup, map[string]any{"name": name, "type": "select", "proxies": []string{previous}})
			previous = name
		}
		return previous
	}
	shared := chain("shared", "leaf")
	longer := chain("longer", shared)
	raw.ProxyGroup = append(raw.ProxyGroup, map[string]any{"name": "group", "type": "fallback", "proxies": []string{shared, longer}})
	raw.ResourceBootstrap = &ResourceBootstrap{Proxy: "group", Timeout: 1}
	scope, err := newResourceBootstrap(context.Background(), raw)
	if scope != nil {
		_ = scope.Close()
	}
	if err == nil || !strings.Contains(err.Error(), "proxy_chain_too_deep") {
		t.Fatalf("memo hid a >64-level path: %v", err)
	}
}
