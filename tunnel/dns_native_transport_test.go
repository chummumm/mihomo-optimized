package tunnel

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/metacubex/mihomo/component/dialer"
	C "github.com/metacubex/mihomo/constant"
)

func TestDNSRoutingNativeDestinationKeepsClientOriginPrivate(t *testing.T) {
	leaf := newDNSProxyTestBase("leaf", C.Socks5, true)
	group := newDNSProxyTestBase("group", C.Selector, true)
	origin := dnsProxyTestResolver(C.UDP)
	origin.Process, origin.ProcessPath = "resolver", "/usr/bin/resolver"
	plan := &DNSRoutingPlan{origin: origin, route: dnsProxyRoute{proxy: leaf, qname: "first.example", groups: []C.ProxyAdapter{group}}}
	before := origin.Clone()
	for _, network := range []string{"tcp", "udp"} {
		metadata, err := plan.nativeDestination(context.Background(), network, "192.0.2.53:443", nil)
		if err != nil {
			t.Fatal(err)
		}
		if metadata.DstIP.String() != "192.0.2.53" || metadata.DstPort != 443 || metadata.NetWork.String() != network ||
			metadata.Host != "" || metadata.SniffHost != "" || metadata.SrcIP.IsValid() || metadata.SrcPort != 0 ||
			metadata.InName != "DNS-TRANSPORT" || metadata.Process != "" || metadata.ProcessPath != "" || metadata.InUser != "" {
			t.Fatalf("resolver transport inherited a logical query: %+v", metadata)
		}
	}
	if !reflect.DeepEqual(origin, before) || !reflect.DeepEqual(plan.OriginMetadata(), before) {
		t.Fatal("transport preparation changed the source used by DNS routing")
	}
	secondPlan := *plan
	secondPlan.origin = origin.Clone()
	secondPlan.origin.SrcPort++
	secondPlan.route.qname = "second.example"
	if plan.TransportKey() != secondPlan.TransportKey() {
		t.Fatal("QNAME/source-port changes unnecessarily split the transport pool")
	}
	secondPlan.epoch++
	if plan.TransportKey() == secondPlan.TransportKey() {
		t.Fatal("runtime epoch changes reused an obsolete transport scope")
	}
}

type dnsNativeKeyTestAdapter struct {
	*dnsProxyTestBase
	info C.ProxyInfo
}

func (p *dnsNativeKeyTestAdapter) ProxyInfo() C.ProxyInfo { return p.info }

func TestDNSNativeTransportIdentityPreservesEffectiveOutboundBoundaries(t *testing.T) {
	leaf := &dnsNativeKeyTestAdapter{dnsProxyTestBase: newDNSProxyTestBase("same-name", C.Socks5, true)}
	firstGroup := newDNSProxyTestBase("first-group", C.Selector, true)
	secondGroup := newDNSProxyTestBase("second-group", C.URLTest, true)
	plan := &DNSRoutingPlan{route: dnsProxyRoute{proxy: newDNSProxyTestProxy(leaf),
		groups: []C.ProxyAdapter{firstGroup}, udpSupportFrozen: true}, epoch: 7}
	other := *plan
	other.route = plan.route
	other.route.proxy = newDNSProxyTestProxy(leaf)
	other.route.groups = []C.ProxyAdapter{secondGroup}
	if plan.NativeTransportKey() != other.NativeTransportKey() {
		t.Fatal("different selection/history wrappers split the same physical outbound")
	}
	if plan.TransportKey() == other.TransportKey() || plan.CacheKey() == other.CacheKey() {
		t.Fatal("native connection reuse weakened the preexisting answer-cache route scope")
	}
	baseline := plan.NativeTransportKey()
	for name, info := range map[string]C.ProxyInfo{
		"interface": {Interface: "different-interface"},
		"mark":      {RoutingMark: 27182},
		"provider":  {ProviderName: "replacement-provider"},
		"options":   {TFO: true, MPTCP: true, XUDP: true, SMUX: true},
	} {
		t.Run(name, func(t *testing.T) {
			leaf.info = info
			if plan.NativeTransportKey() == baseline {
				t.Fatal("different physical outbound options shared an existing transport")
			}
			leaf.info = C.ProxyInfo{}
		})
	}
	other.route.proxy = newDNSProxyTestProxy(&dnsNativeKeyTestAdapter{dnsProxyTestBase: newDNSProxyTestBase(leaf.Name(), C.Socks5, true)})
	if other.NativeTransportKey() == baseline {
		t.Fatal("same-name provider replacement inherited the previous adapter's transport")
	}
	other.route.proxy = plan.route.proxy
	other.epoch++
	if other.NativeTransportKey() == baseline {
		t.Fatal("runtime epoch change reused an obsolete transport")
	}
	other.epoch = plan.epoch
	other.route.udpSupportError = errors.New("selected group disables UDP")
	if other.NativeTransportKey() == baseline {
		t.Fatal("TCP-only selection borrowed a UDP-capable transport")
	}
	other.route.udpSupportError = nil
	for _, kind := range []C.AdapterType{C.Socks5, C.Relay} {
		leaf.tp = kind
		if kind == C.Socks5 {
			leaf.info.DialerProxy = "dynamic-chain"
		} else {
			leaf.info = C.ProxyInfo{}
		}
		if plan.NativeTransportKey() == other.NativeTransportKey() {
			t.Fatal("opaque/dynamic whole-chain identity broadened beyond its old route scope")
		}
	}
}

func TestDNSNativeTransportIdentityUsesEffectiveGlobalSocketOptions(t *testing.T) {
	oldInterface, oldMark := dialer.DefaultInterface.Load(), dialer.DefaultRoutingMark.Load()
	t.Cleanup(func() { dialer.DefaultInterface.Store(oldInterface); dialer.DefaultRoutingMark.Store(oldMark) })
	dialer.DefaultInterface.Store("native-test-first")
	dialer.DefaultRoutingMark.Store(10)
	leaf := &dnsNativeKeyTestAdapter{dnsProxyTestBase: newDNSProxyTestBase("leaf", C.Direct, true)}
	plan := &DNSRoutingPlan{route: dnsProxyRoute{proxy: leaf, udpSupportFrozen: true}}
	first := plan.NativeTransportKey()
	dialer.DefaultInterface.Store("native-test-second")
	if plan.NativeTransportKey() == first {
		t.Fatal("global interface change reused the old bound transport")
	}
	dialer.DefaultInterface.Store("native-test-first")
	dialer.DefaultRoutingMark.Store(20)
	if plan.NativeTransportKey() == first {
		t.Fatal("global routing-mark change reused the old marked transport")
	}
	leaf.info = C.ProxyInfo{Interface: "explicit-interface", RoutingMark: 30}
	explicit := plan.NativeTransportKey()
	dialer.DefaultInterface.Store("native-test-third")
	dialer.DefaultRoutingMark.Store(40)
	if plan.NativeTransportKey() != explicit {
		t.Fatal("unrelated defaults split an explicitly bound outbound")
	}
}
