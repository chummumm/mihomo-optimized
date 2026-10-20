package outboundgroup

import (
	"testing"

	"github.com/metacubex/mihomo/adapter"
	"github.com/metacubex/mihomo/adapter/outbound"
	C "github.com/metacubex/mihomo/constant"
)

type candidateTestProxy struct {
	C.Proxy
	delay uint16
}

func (p *candidateTestProxy) LastDelayForTestUrl(string) uint16 { return p.delay }
func (p *candidateTestProxy) AliveForTestUrl(string) bool       { return true }

func TestResourceBootstrapURLTestCannotReviveExcludedSameNameObject(t *testing.T) {
	proxy := func(name string, delay uint16) C.Proxy {
		return &candidateTestProxy{Proxy: adapter.NewProxy(outbound.NewBase(outbound.BaseOption{Name: name, Type: C.Socks5})), delay: delay}
	}
	excluded := proxy("same", 10)
	other := proxy("other", 50)
	sameNameDifferentProvider := proxy("same", 100)
	group := &URLTest{fastNode: excluded, tolerance: 100}
	chosen := group.SelectFrom([]C.Proxy{other, sameNameDifferentProvider}, nil, true)
	if chosen != other {
		t.Fatalf("selection returned excluded/stale object: %p expected %p", chosen, other)
	}
}
