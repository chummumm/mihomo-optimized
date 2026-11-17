package dns

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/metacubex/mihomo/component/dnsstats"
	C "github.com/metacubex/mihomo/constant"
	"github.com/metacubex/mihomo/tunnel/statistic"
	D "github.com/miekg/dns"
)

type nativePoolTestClient struct{ closed atomic.Int32 }

func (*nativePoolTestClient) ExchangeContext(context.Context, *D.Msg) (*D.Msg, error) {
	return nil, errors.New("pool test does not exchange DNS")
}
func (*nativePoolTestClient) Address() string  { return "pool-test" }
func (*nativePoolTestClient) ResetConnection() {}
func (c *nativePoolTestClient) Close() error   { c.closed.Add(1); return nil }

func TestDNSRuleRoutingNativePoolRetainsActiveSiblings(t *testing.T) {
	pool := newNativeTransportPool(2)
	t.Cleanup(pool.close)
	clients := map[string]*nativePoolTestClient{}
	acquire := func(key string) *nativeTransportEntry {
		t.Helper()
		entry, err := pool.acquire(context.Background(), key, func() dnsClient {
			client := &nativePoolTestClient{}
			clients[key] = client
			return client
		})
		if err != nil {
			t.Fatal(err)
		}
		return entry
	}
	first := acquire("first")
	sibling := acquire("first")
	if first != sibling {
		t.Fatal("same immutable route did not reuse its protocol client")
	}
	old := acquire("old")
	pool.release(old)
	next := acquire("next")
	if clients["old"].closed.Load() != 1 || clients["first"].closed.Load() != 0 {
		t.Fatal("LRU must close only the inactive client")
	}
	pool.release(first)
	pool.release(next)
	last := acquire("last")
	if clients["next"].closed.Load() != 1 || clients["first"].closed.Load() != 0 {
		t.Fatal("one remaining sibling must keep its client alive")
	}
	pool.close()
	select {
	case <-pool.lifetime.Done():
	default:
		t.Fatal("lifecycle shutdown did not cancel active query contexts")
	}
	pool.release(sibling)
	pool.release(last)
	pool.close()
	for key, client := range clients {
		if count := client.closed.Load(); count != 1 {
			t.Errorf("%s was closed %d times", key, count)
		}
	}
	if _, err := pool.acquire(context.Background(), "closed", func() dnsClient {
		t.Fatal("closed pool created a new client")
		return nil
	}); !errors.Is(err, net.ErrClosed) {
		t.Fatalf("closed pool returned %v", err)
	}
}

func TestDNSRuleRoutingNativePoolWaitsWithoutEvictingActiveClient(t *testing.T) {
	pool := newNativeTransportPool(1)
	t.Cleanup(pool.close)
	client := &nativePoolTestClient{}
	active, err := pool.acquire(context.Background(), "active", func() dnsClient { return client })
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if _, err := pool.acquire(ctx, "waiting", func() dnsClient {
		t.Fatal("capacity was exceeded while every client was active")
		return nil
	}); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("blocked acquisition returned %v", err)
	}
	if client.closed.Load() != 0 {
		t.Fatal("waiting for capacity interrupted an active query")
	}
	pool.release(active)
	if _, err := pool.acquire(context.Background(), "replacement", func() dnsClient { return &nativePoolTestClient{} }); err != nil {
		t.Fatal(err)
	}
	if client.closed.Load() != 1 {
		t.Fatal("released client was not eligible for replacement")
	}
}

func TestDNSRuleRoutingNativePoolResetAndClose(t *testing.T) {
	state := &nativeClientState{pool: newNativeTransportPool(1)}
	first := state.pool
	state.reset(false)
	if state.isClosed() || state.pool != nil || first.lifetime.Err() == nil {
		t.Fatal("connection reset must cancel the old pool and permit a fresh pool")
	}
	second := newNativeTransportPool(1)
	state.pool = second
	state.reset(true)
	state.reset(false)
	if !state.isClosed() || state.pool != nil || second.lifetime.Err() == nil {
		t.Fatal("resolver closure must remain terminal after a subsequent reset")
	}
}

func TestDNSRuleRoutingNativeQueriesStayInDNSObservations(t *testing.T) {
	for _, protocol := range []string{"udp53", "h2"} {
		t.Run(protocol, func(t *testing.T) {
			routingTestEnable(t)
			release := make(chan struct{})
			var once sync.Once
			unblock := func() { once.Do(func() { close(release) }) }
			t.Cleanup(unblock)
			var leaf C.ProxyAdapter
			var servers []NameServer
			var seen func()
			if protocol == "udp53" {
				outbound := newRoutingTestOutbound("query-visibility-raw")
				outbound.gate = release
				leaf, servers = outbound, []NameServer{{Addr: "192.0.2.53:53"}}
				seen = func() { routingTestNextQuestion(t, outbound) }
			} else {
				server := newEncryptedRoutingServer(t, protocol, true, func(ctx context.Context, _ string) bool {
					select {
					case <-release:
						return true
					case <-ctx.Done():
						return false
					}
				})
				leaf, servers = newEncryptedRoutingOutbound("query-visibility-h2"), []NameServer{server.ns}
				seen = func() { server.next(t) }
			}
			r := NewResolver(Config{RuleRouting: true, Main: servers}).Resolver
			t.Cleanup(func() { unblock(); r.Close() })
			service := NewService(r, &ResolverEnhancer{mode: C.DNSNormal})
			query := routingTestQuery("query-visibility.example", 71)
			observations := dnsstats.New()
			origin := routingTestOrigin()
			origin.Type, origin.InName = C.INNER, "DNS"
			origin.SrcIP = netip.MustParseAddr("192.168.3.1")
			ctx := dnsstats.WithManager(routingTestContext(leaf, origin), observations)
			result := make(chan error, 1)
			go func() { _, err := service.ServeMsg(ctx, query); result <- err }()
			seen()
			// Observe while the query is still blocked, not after its entry
			// would have disappeared even with the old logical tracker.
			routingTestAssertOnlyTransport(t, leaf.Name())
			unblock()
			select {
			case err := <-result:
				if err != nil {
					t.Fatal(err)
				}
			case <-time.After(3 * time.Second):
				t.Fatal("DNS query did not finish")
			}
			page := observations.Queries(dnsstats.Filter{})
			if len(page.Items) != 1 || page.Items[0].Client != "192.168.3.1" || page.Items[0].QName != "query-visibility.example" || page.Items[0].Source != "DNS" {
				t.Fatalf("hiding the logical connection lost its DNS query record: %+v", page)
			}
			if stats := observations.Upstreams(); len(stats.Items) != 1 || stats.Items[0].Attempts != 1 || stats.Items[0].Successes != 1 {
				t.Fatalf("DNS upstream statistics changed: %+v", stats)
			}
			r.Close()
			statistic.DefaultManager.Range(func(item statistic.Tracker) bool {
				if item.Info().Chain.Last() == leaf.Name() {
					t.Error("resolver shutdown retained its transport tracker")
				}
				return true
			})
		})
	}
}

type nativeLateDialOutbound struct {
	*encryptedRoutingOutbound
	started, canceled, release chan struct{}
	resourceClosed             chan struct{}
	once, closeOnce            sync.Once
	entered                    atomic.Int32
}

func (a *nativeLateDialOutbound) gate(ctx context.Context) {
	a.once.Do(func() {
		close(a.started)
		<-ctx.Done()
		close(a.canceled)
		<-a.release
	})
}

func (a *nativeLateDialOutbound) DialContext(ctx context.Context, md *C.Metadata) (C.Conn, error) {
	a.entered.Add(1) // Count invocation, not eventual completion after cancellation.
	a.gate(ctx)
	// Deliberately emulate an adapter which completes a dial after cancellation.
	conn, err := a.encryptedRoutingOutbound.DialContext(context.Background(), md)
	if err != nil {
		return nil, err
	}
	return &nativeLateTCPConn{Conn: conn, notify: a.notifyClosed}, nil
}

func (a *nativeLateDialOutbound) ListenPacketContext(ctx context.Context, md *C.Metadata) (C.PacketConn, error) {
	a.entered.Add(1)
	a.gate(ctx)
	conn, err := a.encryptedRoutingOutbound.ListenPacketContext(context.Background(), md)
	if err != nil {
		return nil, err
	}
	return &nativeLatePacketConn{PacketConn: conn, notify: a.notifyClosed}, nil
}

func (a *nativeLateDialOutbound) notifyClosed() {
	a.closeOnce.Do(func() { close(a.resourceClosed) })
}

type nativeLateTCPConn struct {
	C.Conn
	notify func()
}

func (c *nativeLateTCPConn) Close() error {
	err := c.Conn.Close()
	c.notify()
	return err
}

type nativeLatePacketConn struct {
	C.PacketConn
	notify func()
}

func (c *nativeLatePacketConn) Close() error {
	err := c.PacketConn.Close()
	c.notify()
	return err
}

func TestDNSRuleRoutingNativeCloseDuringEncryptedConstruction(t *testing.T) {
	for _, protocol := range []string{"h2", "h3", "tls", "quic"} {
		t.Run(protocol, func(t *testing.T) {
			routingTestEnable(t)
			server := newEncryptedRoutingServer(t, protocol, true, nil)
			leaf := &nativeLateDialOutbound{encryptedRoutingOutbound: newEncryptedRoutingOutbound("late-" + protocol),
				started: make(chan struct{}), canceled: make(chan struct{}), release: make(chan struct{}), resourceClosed: make(chan struct{})}
			var release sync.Once
			unblock := func() { release.Do(func() { close(leaf.release) }) }
			t.Cleanup(unblock)
			r := NewResolver(Config{RuleRouting: true, Main: []NameServer{server.ns}}).Resolver
			t.Cleanup(func() { unblock(); r.Close() })
			result := make(chan error, 1)
			go func() {
				_, err := encryptedRoutingRawExchange(r, routingTestContext(leaf, routingTestOrigin()), routingTestQuery("late.example", 81))
				result <- err
			}()
			select {
			case <-leaf.started:
			case <-time.After(time.Second):
				t.Fatal("encrypted construction did not start")
			}
			state, _, _, _ := nativeClientDetails(r.main[0])
			state.mu.Lock()
			pool := state.pool
			state.mu.Unlock()
			pool.mu.Lock()
			var scoped dnsClient
			for _, entry := range pool.entries {
				scoped = entry.client
			}
			pool.mu.Unlock()
			closed := make(chan struct{})
			go func() { r.Close(); close(closed) }()
			select {
			case <-leaf.canceled:
			case <-time.After(time.Second):
				t.Fatal("lifecycle close did not cancel the unfinished construction")
			}
			unblock()
			select {
			case <-closed:
			case <-time.After(time.Second):
				t.Fatal("lifecycle close retained an unfinished encrypted client")
			}
			select {
			case err := <-result:
				if err == nil {
					t.Fatal("late construction completed after shutdown")
				}
			case <-time.After(time.Second):
				t.Fatal("late construction did not unwind")
			}
			// HTTP may finish a request and Close before an old DialContext
			// callback unwinds. Its completion is not a new dial invocation.
			// Explicitly wait for the late socket to be disposed of as well.
			select {
			case <-leaf.resourceClosed:
			case <-time.After(time.Second):
				t.Fatal("late connection was not closed")
			}
			if calls := leaf.entered.Load(); calls != 1 {
				t.Fatalf("unexpected dial invocation count after close: %d", calls)
			}
			if _, err := scoped.ExchangeContext(context.Background(), routingTestQuery("closed.example", 82)); !errors.Is(err, net.ErrClosed) {
				t.Fatalf("closed scoped transport was reusable: %v", err)
			}
			if leaf.entered.Load() != 1 {
				t.Fatal("closed client dialed again")
			}
		})
	}
}

// Force the HTTP/2 ordering which the original test only hit intermittently:
// Close and the request return while an old, canceled adapter call is still
// blocked. Completing that old call must close its socket, not count as redial.
func TestDNSRuleRoutingNativeH2CloseBeforeLateDialReturns(t *testing.T) {
	routingTestEnable(t)
	server := newEncryptedRoutingServer(t, "h2", true, nil)
	leaf := &nativeLateDialOutbound{
		encryptedRoutingOutbound: newEncryptedRoutingOutbound("late-h2-barrier"),
		started:                  make(chan struct{}), canceled: make(chan struct{}),
		release: make(chan struct{}), resourceClosed: make(chan struct{}),
	}
	var once sync.Once
	unblock := func() { once.Do(func() { close(leaf.release) }) }
	r := NewResolver(Config{RuleRouting: true, Main: []NameServer{server.ns}}).Resolver
	t.Cleanup(func() { unblock(); r.Close() })
	result := make(chan error, 1)
	go func() {
		_, err := encryptedRoutingRawExchange(r, routingTestContext(leaf, routingTestOrigin()), routingTestQuery("late-h2.example", 83))
		result <- err
	}()
	wait := func(ch <-chan struct{}, what string) {
		t.Helper()
		select {
		case <-ch:
		case <-time.After(5 * time.Second):
			t.Fatal(what)
		}
	}
	wait(leaf.started, "initial dial did not start")
	state, _, _, _ := nativeClientDetails(r.main[0])
	state.mu.Lock()
	pool := state.pool
	state.mu.Unlock()
	pool.mu.Lock()
	var scoped dnsClient
	for _, entry := range pool.entries {
		scoped = entry.client
	}
	pool.mu.Unlock()
	if scoped == nil {
		t.Fatal("missing scoped HTTP/2 client")
	}
	closed := make(chan struct{})
	go func() { r.Close(); close(closed) }()
	wait(leaf.canceled, "close did not cancel the detached HTTP dial")
	// Do not unblock the old adapter until both caller-visible operations end.
	wait(closed, "HTTP/2 Close waited for the deliberately uncooperative adapter")
	select {
	case err := <-result:
		if err == nil {
			t.Fatal("canceled request succeeded")
		}
	case <-time.After(5 * time.Second):
		t.Fatal("HTTP/2 request failed to observe cancellation")
	}
	if leaf.entered.Load() != 1 || leaf.count() != 0 {
		t.Fatal("test failed to isolate an in-flight dial from its completion")
	}
	if _, err := scoped.ExchangeContext(context.Background(), routingTestQuery("after-close.example", 84)); !errors.Is(err, net.ErrClosed) {
		t.Fatalf("closed client accepted new work: %v", err)
	}
	if leaf.entered.Load() != 1 {
		t.Fatal("closed client started a new adapter call")
	}
	unblock()
	wait(leaf.resourceClosed, "late connection leaked after request cancellation")
	if leaf.entered.Load() != 1 || leaf.count() != 1 {
		t.Fatal("expected exactly one old dial to finish and be closed")
	}
	select {
	case query := <-server.seen:
		t.Fatalf("closed transport sent a DNS query: %+v", query)
	default:
	}
}
