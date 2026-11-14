package dns_test

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/mihomo/adapter/outbound"
	N "github.com/metacubex/mihomo/common/net"
	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/dns"
	M "github.com/metacubex/sing/common/metadata"
	"github.com/metacubex/sing/common/uot"
	D "github.com/miekg/dns"
)

type legacyUOTDNSWrite struct {
	payloadLength int
	written       int
	err           error
}

type legacyUOTDNSPacketConn struct {
	C.PacketConn
	writes chan<- legacyUOTDNSWrite
}

func (c *legacyUOTDNSPacketConn) WriteTo(payload []byte, destination net.Addr) (int, error) {
	n, err := c.PacketConn.WriteTo(payload, destination)
	c.writes <- legacyUOTDNSWrite{payloadLength: len(payload), written: n, err: err}
	return n, err
}

type legacyUOTDNSOutbound struct {
	*outbound.Base
	dials  atomic.Int32
	writes chan legacyUOTDNSWrite
	errors chan error
	mu     sync.Mutex
	peers  []net.Conn
	closed bool
	wg     sync.WaitGroup
}

func (a *legacyUOTDNSOutbound) SupportUOT() bool { return true }

func (a *legacyUOTDNSOutbound) ListenPacketContext(ctx context.Context, metadata *C.Metadata) (C.PacketConn, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	a.mu.Lock()
	if a.closed {
		a.mu.Unlock()
		return nil, net.ErrClosed
	}
	client, server := net.Pipe()
	a.peers = append(a.peers, server)
	a.wg.Add(1)
	a.mu.Unlock()
	request := uot.Request{Destination: M.SocksaddrFromNet(metadata.UDPAddr())}
	a.dials.Add(1)
	go a.serve(server, request)

	// This real outbound wrapper hides vectorised writes behind ExtendedConn,
	// just like the legacy Shadowsocks/TUIC UOT paths that exposed the bug.
	stream := outbound.NewConn(client, a)
	packet := N.NewUOTConn(stream, request)
	conn := outbound.NewPacketConn(N.NewThreadSafePacketConn(packet), a)
	return &legacyUOTDNSPacketConn{PacketConn: conn, writes: a.writes}, nil
}

func (a *legacyUOTDNSOutbound) serve(conn net.Conn, request uot.Request) {
	defer a.wg.Done()
	defer conn.Close()
	peer := uot.NewConn(conn, request)
	wire := make([]byte, 4096)
	for {
		n, destination, err := peer.ReadFrom(wire)
		if err != nil {
			a.recordError(err)
			return
		}
		if destination.String() != request.Destination.String() {
			a.recordError(fmt.Errorf("UOT destination = %s, want %s", destination, request.Destination))
			return
		}
		query := new(D.Msg)
		if err := query.Unpack(wire[:n]); err != nil {
			a.recordError(err)
			return
		}
		if len(query.Question) != 1 || query.Question[0].Qtype != D.TypeA {
			a.recordError(fmt.Errorf("unexpected DNS question: %v", query.Question))
			return
		}
		answer := new(D.Msg).SetReply(query)
		answer.Answer = []D.RR{&D.A{
			Hdr: D.RR_Header{Name: query.Question[0].Name, Rrtype: D.TypeA, Class: D.ClassINET, Ttl: 60},
			A:   net.IPv4(192, 0, 2, 99),
		}}
		response, err := answer.Pack()
		if err == nil {
			_, err = peer.WriteTo(response, destination)
		}
		if err != nil {
			a.recordError(err)
			return
		}
	}
}

func (a *legacyUOTDNSOutbound) recordError(err error) {
	if errors.Is(err, io.EOF) || errors.Is(err, io.ErrClosedPipe) || errors.Is(err, net.ErrClosed) {
		return
	}
	select {
	case a.errors <- err:
	default:
	}
}

func (a *legacyUOTDNSOutbound) close() {
	a.mu.Lock()
	a.closed = true
	for _, peer := range a.peers {
		_ = peer.Close()
	}
	a.mu.Unlock()
	a.wg.Wait()
}

func TestDNSClientLegacyUOTSharedAndDedicated(t *testing.T) {
	for _, test := range []struct {
		name         string
		disableReuse bool
		wantDials    int32
	}{
		{name: "shared", wantDials: 1},
		{name: "dedicated", disableReuse: true, wantDials: 2},
	} {
		t.Run(test.name, func(t *testing.T) {
			upstream := &legacyUOTDNSOutbound{
				Base: outbound.NewBase(outbound.BaseOption{
					Name: "legacy-uot-dns", Addr: "127.0.0.1:1", Type: C.Shadowsocks, UDP: true,
				}),
				writes: make(chan legacyUOTDNSWrite, 8),
				errors: make(chan error, 8),
			}
			t.Cleanup(upstream.close)
			params := map[string]string{}
			if test.disableReuse {
				params["disable-reuse"] = "true"
			}
			resolvers := dns.NewResolver(dns.Config{
				Main: []dns.NameServer{{
					Net: "udp", Addr: "192.0.2.53:53", ProxyAdapter: upstream, Params: params,
				}},
				SpeedCheck: dns.SpeedCheckConfig{Mode: []string{}},
			})
			t.Cleanup(resolvers.Close)
			ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
			defer cancel()
			for i := 0; i < 2; i++ {
				query := new(D.Msg).SetQuestion(fmt.Sprintf("uot-%d.example.", i), D.TypeA)
				query.Id = uint16(100 + i)
				answer, err := resolvers.ExchangeContext(ctx, query)
				if err != nil {
					t.Fatalf("query %d over legacy UOT: %v", i, err)
				}
				if answer == nil || answer.Id != query.Id || answer.Rcode != D.RcodeSuccess || len(answer.Answer) != 1 {
					t.Fatalf("query %d returned an invalid answer: %v", i, answer)
				}
				address, ok := answer.Answer[0].(*D.A)
				if !ok || address.Hdr.Name != query.Question[0].Name || !address.A.Equal(net.IPv4(192, 0, 2, 99)) {
					t.Fatalf("query %d returned the wrong address: %v", i, answer)
				}
				// A fast reply can race the shared writer's failure handling.
				// Check the actual returned count as well as the DNS result.
				select {
				case write := <-upstream.writes:
					if write.err != nil || write.written != write.payloadLength {
						t.Fatalf("query %d write: n=%d, payload=%d, err=%v", i, write.written, write.payloadLength, write.err)
					}
				case <-ctx.Done():
					t.Fatal("DNS reply arrived without a completed UOT write")
				}
			}
			if got := upstream.dials.Load(); got != test.wantDials {
				t.Errorf("UOT connections = %d, want %d", got, test.wantDials)
			}
			select {
			case err := <-upstream.errors:
				t.Errorf("UOT server: %v", err)
			default:
			}
		})
	}
}
