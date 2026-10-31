package adapter

import (
	"context"
	"net/netip"
	"testing"

	"github.com/metacubex/mihomo/adapter/outbound"
	"github.com/metacubex/mihomo/component/lanpreconnect"
	C "github.com/metacubex/mihomo/constant"
)

type selectionTestAdapter struct {
	*outbound.Base
	unwraps int
}

func (a *selectionTestAdapter) PrepareLAN(context.Context, netip.Addr) error { return nil }
func (a *selectionTestAdapter) Unwrap(*C.Metadata, bool) C.Proxy             { a.unwraps++; return nil }

type selectionTestWrapper struct{ C.ProxyAdapter }

func (w *selectionTestWrapper) UnderlyingProxyAdapter() C.ProxyAdapter { return w.ProxyAdapter }

func TestLANSelectionOnlySealsActualBusinessLeaf(t *testing.T) {
	group := &selectionTestAdapter{Base: outbound.NewBase(outbound.BaseOption{Name: "load-balance", Type: C.LoadBalance})}
	leaf := &selectionTestAdapter{Base: outbound.NewBase(outbound.BaseOption{Name: "lan", Type: C.WireGuard})}
	underlay := &selectionTestAdapter{Base: outbound.NewBase(outbound.BaseOption{Name: "underlay", Type: C.WireGuard})}
	ctx := lanpreconnect.Business(context.Background())
	ctx = selectLANOutbound(ctx, group)
	if !lanpreconnect.NeedsSelection(ctx) {
		t.Fatal("group was mistaken for actual leaf")
	}
	ctx = selectLANOutbound(ctx, &selectionTestWrapper{leaf})
	ctx = selectLANOutbound(ctx, underlay)
	if !lanpreconnect.Selected(ctx, leaf) || lanpreconnect.Selected(ctx, underlay) {
		t.Fatal("nested endpoint replaced actual LAN target owner")
	}
	if group.unwraps != 0 || leaf.unwraps != 0 {
		t.Fatal("observation advanced group selection")
	}
	if lanpreconnect.Selected(selectLANOutbound(context.Background(), leaf), leaf) {
		t.Fatal("health probe entered business observation")
	}
	// Existing non-business callers do not have to supply optional adapter APIs.
	selectLANOutbound(context.Background(), nil)
}
