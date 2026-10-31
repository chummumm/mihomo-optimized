package adapter

import (
	"context"

	"github.com/metacubex/mihomo/component/lanpreconnect"
	C "github.com/metacubex/mihomo/constant"
)

// Seal the observation at the actual leaf selected by the normal group call.
// A dialer-proxy's private server address must not become a business LAN target.
// This never invokes Unwrap and never advances a load-balancer a second time.
func selectLANOutbound(ctx context.Context, a C.ProxyAdapter) context.Context {
	if !lanpreconnect.NeedsSelection(ctx) {
		return ctx
	}
	switch a.Type() {
	case C.Selector, C.Fallback, C.URLTest, C.LoadBalance, C.Relay:
		return ctx
	}
	for i := 0; i < 16; i++ {
		wrapper, ok := a.(interface{ UnderlyingProxyAdapter() C.ProxyAdapter })
		if !ok {
			break
		}
		next := wrapper.UnderlyingProxyAdapter()
		if next == nil || next == a {
			break
		}
		a = next
	}
	preparer, _ := a.(lanpreconnect.Preparer)
	return lanpreconnect.Select(ctx, preparer)
}
