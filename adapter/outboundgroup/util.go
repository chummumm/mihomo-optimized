package outboundgroup

import (
	"context"
	"time"

	"github.com/metacubex/mihomo/common/utils"
	C "github.com/metacubex/mihomo/constant"
	P "github.com/metacubex/mihomo/constant/provider"
)

type ProxyGroup interface {
	C.ProxyAdapter

	Providers() []P.ProxyProvider
	Proxies() []C.Proxy
	Now() string
	Touch()

	Hidden() bool
	Icon() string

	URLTest(ctx context.Context, url string, expectedStatus utils.IntRanges[uint16]) (mp map[string]uint16, err error)
}

var _ ProxyGroup = (*Fallback)(nil)
var _ ProxyGroup = (*LoadBalance)(nil)
var _ ProxyGroup = (*URLTest)(nil)
var _ ProxyGroup = (*Selector)(nil)

type SelectAble interface {
	Set(string) error
	ForceSet(name string)
}

// CandidateSelector shares the ordinary group policy with isolated callers
// that already own a filtered snapshot. It neither dials nor starts workers.
type CandidateSelector interface {
	SelectFrom(proxies []C.Proxy, metadata *C.Metadata, touch bool) C.Proxy
}

// ProbeOptions exposes the normal group's test policy without starting its
// runtime health workers. Select groups deliberately have no automatic probe.
func ProbeOptions(group ProxyGroup) (string, utils.IntRanges[uint16], time.Duration) {
	var url, status string
	var timeout int
	switch g := group.(type) {
	case *Fallback:
		url, status, timeout = g.testUrl, g.expectedStatus, g.testTimeout
	case *URLTest:
		url, status, timeout = g.testUrl, g.expectedStatus, g.testTimeout
	case *LoadBalance:
		url, status, timeout = g.testUrl, g.expectedStatus, g.testTimeout
	default:
		return "", nil, 0
	}
	expected, _ := utils.NewUnsignedRanges[uint16](status)
	return url, expected, time.Duration(timeout) * time.Millisecond
}

var _ SelectAble = (*Fallback)(nil)
var _ SelectAble = (*URLTest)(nil)
var _ SelectAble = (*Selector)(nil)
