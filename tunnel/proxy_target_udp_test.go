package tunnel

import (
	"context"
	"fmt"
	"net/netip"
	"testing"

	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/component/trie"
	C "github.com/metacubex/mihomo/constant"
)

type targetPolicyUDPConn struct{ *directUDPTestPacketConn }

func (*targetPolicyUDPConn) ForceIPPolicy() bool { return true }

// The established connection reaches packetSender through an accounting
// wrapper. Policy must come from that connection, not the currently selected
// group or a global value that can change on reload.
type targetPolicyUDPAccounting struct{ C.PacketConn }

func (c *targetPolicyUDPAccounting) Upstream() any { return c.PacketConn }

func TestProxyTargetUDPNewAddressRetainsHostsCandidates(t *testing.T) {
	oldHosts, oldMapper := resolver.DefaultHosts, resolver.DefaultHostMapper
	t.Cleanup(func() { resolver.DefaultHosts, resolver.DefaultHostMapper = oldHosts, oldMapper })
	clientIP := netip.MustParseAddr("192.0.2.10")
	wantIP := netip.MustParseAddr("203.0.113.10")
	hosts := trie.New[resolver.HostValue]()
	if err := hosts.Insert("rewrite.example", resolver.HostValue{IPs: []netip.Addr{
		netip.MustParseAddr("2001:db8::10"), wantIP,
	}}); err != nil {
		t.Fatal(err)
	}
	resolver.DefaultHosts = resolver.NewHosts(hosts)
	resolver.DefaultHostMapper = &directUDPTestMapper{hosts: map[netip.Addr]string{clientIP: "rewrite.example"}}
	transport := &directUDPTestPacketConn{resolve: func(ctx context.Context, metadata *C.Metadata) error {
		if metadata.Host != "rewrite.example" || !metadata.DestinationOverride {
			return fmt.Errorf("hosts candidates lost before target resolution: %#v", metadata)
		}
		ips, err := resolver.LookupIPv4WithResolver(ctx, metadata.Host, nil)
		if err != nil {
			return err
		}
		if len(ips) == 0 {
			return C.ErrNoProxyTarget
		}
		metadata.DstIP = ips[0]
		return nil
	}}
	pc := &targetPolicyUDPAccounting{PacketConn: &targetPolicyUDPConn{transport}}
	sender := newPacketSender().(*packetSender)
	defer sender.Close()
	metadata := &C.Metadata{DstIP: clientIP, DstPort: 4444, NetWork: C.UDP}
	fixMetadata(metadata)
	for range 2 {
		sender.processPacket(pc, C.NewPacketAdapter(&directUDPTestPacket{}, metadata))
	}
	if transport.calls != 1 || len(transport.written) != 2 {
		t.Fatalf("new target resolution/mapping failed: calls=%d writes=%v", transport.calls, transport.written)
	}
	for _, address := range transport.written {
		if address.Addr() != wantIP || address.Port() != 4444 {
			t.Fatalf("hosts family selection changed: %s", address)
		}
	}
	if metadata.Host != "" || metadata.DstIP != clientIP || sender.RestoreReadFrom(wantIP) != clientIP {
		t.Fatal("original packet or reply address was changed")
	}
}
