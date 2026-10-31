package tunnel_test

import (
	"context"
	"net/netip"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/mihomo/adapter"
	"github.com/metacubex/mihomo/adapter/outbound"
	"github.com/metacubex/mihomo/adapter/outboundgroup"
	"github.com/metacubex/mihomo/component/lanpreconnect"
	C "github.com/metacubex/mihomo/constant"
	P "github.com/metacubex/mihomo/constant/provider"
	R "github.com/metacubex/mihomo/rules"
	"github.com/metacubex/mihomo/tunnel"
)

type selectedLANAdapter struct {
	*outbound.Base
	prepared chan netip.Addr
	unwraps  atomic.Int32
}

func (a *selectedLANAdapter) PrepareLAN(_ context.Context, target netip.Addr) error {
	a.prepared <- target
	return nil
}
func (a *selectedLANAdapter) Unwrap(*C.Metadata, bool) C.Proxy { a.unwraps.Add(1); return nil }

type selectedLANProvider struct {
	P.ProxyProvider
	proxies []C.Proxy
}

func (p *selectedLANProvider) Proxies() []C.Proxy         { return p.proxies }
func (p *selectedLANProvider) Version() uint32            { return 1 }
func (p *selectedLANProvider) VehicleType() P.VehicleType { return P.Compatible }

func TestLANRefreshUsesRealSelectorBeforeBusiness(t *testing.T) {
	first := &selectedLANAdapter{Base: outbound.NewBase(outbound.BaseOption{Name: "first", Type: C.WireGuard}), prepared: make(chan netip.Addr, 2)}
	second := &selectedLANAdapter{Base: outbound.NewBase(outbound.BaseOption{Name: "second", Type: C.WireGuard}), prepared: make(chan netip.Addr, 2)}
	firstProxy, secondProxy := adapter.NewProxy(first), adapter.NewProxy(second)
	provider := &selectedLANProvider{proxies: []C.Proxy{firstProxy, secondProxy}}
	selector, err := outboundgroup.NewSelector(outboundgroup.GroupCommonOption{Name: "manual"}, outboundgroup.SelectorOption{DefaultSelected: "second"}, firstProxy, []P.ProxyProvider{provider})
	if err != nil {
		t.Fatal(err)
	}
	rule, err := R.ParseRule("IP-CIDR", "192.168.42.0/24", "manual", []string{"no-resolve"}, nil)
	if err != nil {
		t.Fatal(err)
	}
	oldManager, oldMode := lanpreconnect.Default, tunnel.Mode()
	oldProxies, oldProviders, oldRules, oldRuleProviders := tunnel.Proxies(), tunnel.Providers(), tunnel.Rules(), tunnel.RuleProviders()
	manager := lanpreconnect.New()
	lanpreconnect.Default = manager
	t.Cleanup(func() {
		manager.Close()
		tunnel.UpdateProxies(oldProxies, oldProviders)
		tunnel.UpdateRules(oldRules, nil, oldRuleProviders)
		tunnel.SetMode(oldMode)
		lanpreconnect.Default = oldManager
	})
	tunnel.UpdateProxies(map[string]C.Proxy{"manual": adapter.NewProxy(selector)}, nil)
	tunnel.UpdateRules([]C.Rule{rule}, nil, nil)
	tunnel.SetMode(tunnel.Rule)
	tunnel.RefreshLANPreconnect()
	select {
	case target := <-second.prepared:
		if target.IsValid() {
			t.Fatal("prefix was converted into fabricated target")
		}
	case <-time.After(time.Second):
		t.Fatal("actual selector did not prepare selected leaf before business")
	}
	select {
	case <-first.prepared:
		t.Fatal("unselected instance was started")
	default:
	}
	if err = selector.Set("first"); err != nil {
		t.Fatal(err)
	}
	tunnel.RefreshLANPreconnect()
	select {
	case <-first.prepared:
	case <-time.After(time.Second):
		t.Fatal("selection change did not refresh intent")
	}
	if first.unwraps.Load() != 0 || second.unwraps.Load() != 0 {
		t.Fatal("preparation performed extra Unwrap")
	}
}
