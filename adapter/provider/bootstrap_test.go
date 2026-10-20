package provider

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	C "github.com/metacubex/mihomo/constant"
)

func TestResourceBootstrapMappingSnapshotsPreserveLiveParser(t *testing.T) {
	prefix := "p"
	override := overrideSchema{AdditionalPrefix: &prefix}
	parse, err := NewProxiesParser("fixture", nil, "a`a", "", "", "", override, "")
	if err != nil {
		t.Fatal(err)
	}
	nodes, err := parse([]byte("proxies: [{name: a, type: socks5, server: 127.0.0.1, port: 1080}]"))
	if err != nil {
		t.Fatal(err)
	}
	defer func() {
		for _, node := range nodes {
			_ = node.Close()
		}
	}()
	if len(nodes) != 2 || nodes[0].Name() != "pa" || nodes[1].Name() != "ppa" {
		t.Fatalf("multi-filter overrides no longer capture distinct selections: %v", nodes)
	}
}

func TestResourceBootstrapProviderCacheTypesAndFallback(t *testing.T) {
	oldHome := C.Path.HomeDir()
	C.SetHomeDir(t.TempDir())
	t.Cleanup(func() { C.SetHomeDir(oldHome) })
	node := map[string]any{"name": "inline", "type": "direct"}
	path := filepath.Join(C.Path.HomeDir(), "nodes.yaml")
	if err := os.WriteFile(path, []byte("proxies: [{name: cached, type: direct}]"), 0600); err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		mapping map[string]any
		name    string
	}{
		{map[string]any{"type": "file", "path": path}, "cached"},
		{map[string]any{"type": "http", "path": path, "url": "http://127.0.0.1:1/subscription"}, "cached"},
		{map[string]any{"type": "inline", "payload": []map[string]any{node}}, "inline"},
		{map[string]any{"type": "http", "path": filepath.Join(C.Path.HomeDir(), "missing"), "url": "http://127.0.0.1:1/subscription", "payload": []map[string]any{node}}, "inline"},
	} {
		nodes, err := BootstrapProxyMappings(context.Background(), tc.mapping)
		if err != nil || len(nodes) != 1 || nodes[0]["name"] != tc.name {
			t.Fatalf("cached provider %v = %#v %v", tc.mapping["type"], nodes, err)
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := BootstrapProxyMappings(ctx, map[string]any{"type": "file", "path": path}); err == nil {
		t.Fatal("cancelled cache inspection continued")
	}
}
