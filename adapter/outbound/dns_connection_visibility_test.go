package outbound

import (
	"context"
	"fmt"
	"net/netip"
	"testing"

	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/tunnel/statistic"
)

// An outer wrapper cannot erase the terminal DNS service's identity. This
// also exercises the traversal used when proxy groups/ref wrappers are added.
type dnsVisibilityTCPWrapper struct{ C.Conn }

func (*dnsVisibilityTCPWrapper) LocalDNS() bool  { return false }
func (c *dnsVisibilityTCPWrapper) Upstream() any { return c.Conn }

type dnsVisibilityUDPWrapper struct{ C.PacketConn }

func (*dnsVisibilityUDPWrapper) LocalDNS() bool  { return false }
func (c *dnsVisibilityUDPWrapper) Upstream() any { return c.PacketConn }

func TestDNSObservabilityLocalConnectionVisibilityUsesActualTerminal(t *testing.T) {
	for _, network := range []C.NetWork{C.TCP, C.UDP} {
		for _, port := range []uint16{53, 1053} {
			for _, wrapped := range []bool{false, true} {
				for _, kind := range []string{"local", "forwarded", "upstream"} {
					t.Run(fmt.Sprintf("%s/%d/wrapped=%t/%s", network, port, wrapped, kind), func(t *testing.T) {
						manager := &statistic.Manager{}
						metadata := &C.Metadata{Type: C.INNER, NetWork: network, InName: "DNS",
							SrcIP: netip.MustParseAddr("192.168.3.1"), SrcPort: 41000,
							DstIP: netip.MustParseAddr("192.0.2.53"), DstPort: port}
						local := NewDnsWithOption(DnsOption{Name: "custom-resolver-service"})
						// A DNS-looking name and port must never hide an external flow.
						remote := NewDirectWithOption(DirectOption{Name: "DNS"})
						group := NewBase(BaseOption{Name: "selected-group", Type: C.Selector})
						var tracker statistic.Tracker
						if network == C.TCP {
							var conn C.Conn
							var err error
							if kind == "local" {
								conn, err = local.DialContext(context.Background(), metadata)
							} else {
								conn = NewConn(nopConn{}, remote)
							}
							if err != nil {
								t.Fatal(err)
							}
							if wrapped {
								conn.AppendToChains(group)
								conn = &dnsVisibilityTCPWrapper{conn}
							}
							if kind == "upstream" {
								tracker = statistic.NewDNSTCPTracker(conn, manager, metadata, nil, 11, 17, true)
							} else {
								tracker = statistic.NewTCPTracker(conn, manager, metadata, nil, 11, 17, true)
							}
						} else {
							var conn C.PacketConn
							var err error
							if kind == "local" {
								conn, err = local.ListenPacketContext(context.Background(), metadata)
							} else {
								conn = NewPacketConn(&nopPacketConn{}, remote)
							}
							if err != nil {
								t.Fatal(err)
							}
							if wrapped {
								conn.AppendToChains(group)
								conn = &dnsVisibilityUDPWrapper{conn}
							}
							if kind == "upstream" {
								tracker = statistic.NewDNSUDPTracker(conn, manager, metadata, nil, 11, 17, true)
							} else {
								tracker = statistic.NewUDPTracker(conn, manager, metadata, nil, 11, 17, true)
							}
						}
						t.Cleanup(func() { _ = tracker.Close() })
						visible := kind != "local"
						if manager.Get(tracker.ID()) != tracker {
							t.Fatal("visibility removed a live connection from lifecycle management")
						}
						registered := 0
						manager.Range(func(item statistic.Tracker) bool {
							registered++
							if item != tracker {
								t.Fatal("registry returned an unrelated connection")
							}
							return true
						})
						if registered != 1 {
							t.Fatalf("bulk lifecycle management sees %d connections, want one", registered)
						}
						if got := len(manager.Snapshot().Connections); (got == 1) != visible || got > 1 {
							t.Fatalf("unexpected connections snapshot: %d", got)
						}
						if tracker.Info().DNS != (kind == "upstream") {
							t.Fatal("visibility changed the explicit DNS upstream marker")
						}
						if up, down := manager.Total(); up != 11 || down != 17 {
							t.Fatalf("visibility changed byte accounting: %d/%d", up, down)
						}
						// Provider replacement and the bulk-close API enumerate
						// the registry, including connections hidden from snapshots.
						manager.Range(func(item statistic.Tracker) bool {
							_ = item.Close()
							return true
						})
						if manager.Get(tracker.ID()) != nil || len(manager.Snapshot().Connections) != 0 {
							t.Fatal("bulk close retained the connection")
						}
					})
				}
			}
		}
	}
}
