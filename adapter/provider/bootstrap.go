package provider

import (
	"context"
	"fmt"
	"os"

	"github.com/metacubex/mihomo/common/structure"
	"github.com/metacubex/mihomo/common/yaml"
	"github.com/metacubex/mihomo/component/desktop"
	C "github.com/metacubex/mihomo/constant"
)

// BootstrapProxyMappings reads only the explicitly selected provider's existing
// cache or inline fallback. It never downloads a subscription or calls Initial,
// and constructs no adapters or health-check workers.
func BootstrapProxyMappings(ctx context.Context, mapping map[string]any) ([]map[string]any, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	schema := &proxyProviderSchema{}
	decoder := structure.NewDecoder(structure.Option{TagName: "provider", WeaklyTypedInput: true})
	if err := decoder.Decode(mapping, schema); err != nil {
		return nil, err
	}
	parse, err := newProxyMappingsParser(schema.Filter, schema.ExcludeFilter, schema.ExcludeType, schema.DialerProxy, schema.Override, schema.AgeSecretKey)
	if err != nil {
		return nil, err
	}
	var path string
	switch schema.Type {
	case "file":
		path = C.Path.Resolve(schema.Path)
	case "http":
		path = C.Path.GetPathByHash("proxies", schema.URL)
		if schema.Path != "" {
			path = C.Path.Resolve(schema.Path)
		}
	case "inline":
	default:
		return nil, fmt.Errorf("unsupported bootstrap provider type")
	}
	var cacheErr error
	if path != "" {
		if !C.Path.IsSafePath(path) {
			return nil, C.Path.ErrNotSafePath(path)
		}
		if err := desktop.CheckPath(path); err != nil {
			return nil, err
		}
		data, err := os.ReadFile(path)
		cacheErr = err
		if err == nil {
			if err := ctx.Err(); err != nil {
				return nil, err
			}
			nodes, err := parse(data)
			if err == nil {
				return nodes, nil
			}
			cacheErr = err
		}
	}
	if len(schema.Payload) != 0 {
		data, err := yaml.Marshal(ProxySchema{Proxies: schema.Payload})
		if err != nil {
			return nil, err
		}
		return parse(data)
	}
	if cacheErr != nil {
		return nil, cacheErr
	}
	return nil, fmt.Errorf("bootstrap provider has no existing nodes")
}
