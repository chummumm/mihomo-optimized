package provider

import (
	"context"
	"fmt"
	"os"

	"github.com/metacubex/mihomo/common/structure"
	"github.com/metacubex/mihomo/common/utils"
	"github.com/metacubex/mihomo/common/yaml"
	"github.com/metacubex/mihomo/component/desktop"
	C "github.com/metacubex/mihomo/constant"
	P "github.com/metacubex/mihomo/constant/provider"
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

// NewBootstrapProvider preserves provider identity/filter behavior without a
// fetcher or permanent health-check lifecycle. The caller owns its immutable
// proxy snapshot and performs bounded checks with the resource request context.
func NewBootstrapProvider(name string, mapping map[string]any, proxies []C.Proxy) (P.ProxyProvider, error) {
	schema := &proxyProviderSchema{}
	decoder := structure.NewDecoder(structure.Option{TagName: "provider", WeaklyTypedInput: true})
	if err := decoder.Decode(mapping, schema); err != nil {
		return nil, err
	}
	var vehicle P.VehicleType
	switch schema.Type {
	case "file":
		vehicle = P.File
	case "http":
		vehicle = P.HTTP
	case "inline":
		vehicle = P.Inline
	default:
		return nil, fmt.Errorf("unsupported bootstrap provider type")
	}
	status, err := utils.NewUnsignedRanges[uint16](schema.HealthCheck.ExpectedStatus)
	if err != nil {
		return nil, err
	}
	return &bootstrapProvider{baseProvider: baseProvider{
		name: name, proxies: proxies,
		healthCheck: NewHealthCheck(proxies, schema.HealthCheck.URL, uint(schema.HealthCheck.TestTimeout), 0, true, status),
	}, vehicle: vehicle}, nil
}

type bootstrapProvider struct {
	baseProvider
	vehicle P.VehicleType
}

func (p *bootstrapProvider) VehicleType() P.VehicleType { return p.vehicle }
func (p *bootstrapProvider) Initial() error {
	return fmt.Errorf("bootstrap provider is an immutable snapshot")
}
func (p *bootstrapProvider) Update() error {
	return fmt.Errorf("bootstrap provider is an immutable snapshot")
}
func (p *bootstrapProvider) HealthCheck()                                                          {}
func (p *bootstrapProvider) Touch()                                                                {}
func (p *bootstrapProvider) RegisterHealthCheckTask(string, utils.IntRanges[uint16], string, uint) {}
