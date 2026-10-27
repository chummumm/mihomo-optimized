//go:build !no_easytier

package outbound

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"time"

	"github.com/metacubex/mihomo/component/easytier"
	icontext "github.com/metacubex/mihomo/context"

	corehost "github.com/easytier/easytier/easytier-go"
)

const easyTierRoutePoll = 200 * time.Millisecond

var errEasyTierInstanceChanged = errors.New("easytier instance changed")

// Keep the lifecycle/route boundary explicit: Running only describes the
// embedded runtime, not DHCP, a connected path, or this request's destination.
type easyTierInstance interface {
	State() corehost.State
	Wait(context.Context) error
	Events() <-chan corehost.Event
	ShowNodeInfo(context.Context) (*corehost.NodeInfo, error)
	ListRoute(context.Context) ([]*corehost.Route, error)
	Dial(context.Context, string, string) (net.Conn, error)
	ListenPacket(string, string) (net.PacketConn, error)
	Close(context.Context) error
}

type easyTierRouteSnapshot struct {
	info   *corehost.NodeInfo
	routes []*corehost.Route
	at     time.Time
	err    error
}

// routeSnapshot shares one bounded RPC refresh across all waiting clients.
// Its lifetime belongs to the outbound; canceling one waiter does not cancel
// the refresh or the instance used by other clients.
func (e *EasyTier) routeSnapshot(ctx context.Context, instance easyTierInstance) (*easyTierRouteSnapshot, error) {
	for {
		e.mu.Lock()
		if e.closed {
			e.mu.Unlock()
			return nil, errEasyTierClosed
		}
		if e.instance != instance {
			e.mu.Unlock()
			return nil, errEasyTierInstanceChanged
		}
		if snapshot := e.routeState; snapshot != nil && time.Since(snapshot.at) < easyTierRoutePoll {
			e.mu.Unlock()
			return snapshot, snapshot.err
		}
		if e.routeRead == nil {
			e.routeRead = make(chan struct{})
			go e.refreshRoutes(instance, e.routeRead, e.routeEpoch)
		}
		done := e.routeRead
		e.mu.Unlock()
		select {
		case <-ctx.Done():
			return nil, icontext.TCPDialError(ctx, ctx.Err())
		case <-e.ctx.Done():
			return nil, errEasyTierClosed
		case <-done:
		}
	}
}

func (e *EasyTier) refreshRoutes(instance easyTierInstance, done chan struct{}, epoch uint64) {
	ctx, cancel := context.WithTimeout(e.ctx, time.Second)
	defer cancel()
	snapshot := &easyTierRouteSnapshot{}
	snapshot.info, snapshot.err = instance.ShowNodeInfo(ctx)
	if snapshot.err == nil {
		snapshot.routes, snapshot.err = instance.ListRoute(ctx)
	}
	snapshot.at = time.Now()
	e.mu.Lock()
	if !e.closed && e.instance == instance && e.routeEpoch == epoch {
		e.routeState = snapshot
	}
	if e.routeRead == done {
		e.routeRead = nil
	}
	close(done)
	e.mu.Unlock()
}

func (e *EasyTier) prepareTarget(ctx context.Context, host string) (easyTierInstance, netip.Addr, error) {
	if err := e.ensureStarted(ctx); err != nil {
		return nil, netip.Addr{}, fmt.Errorf("easytier: wait for runtime: %w", err)
	}
	var ip netip.Addr
	if literal, err := netip.ParseAddr(host); err == nil {
		ip = literal.Unmap()
		if !ip.Is4() {
			return nil, netip.Addr{}, errors.New("easytier: overlay dial supports IPv4 only")
		}
	}
	// A missing MagicDNS name may simply be a route that has not arrived yet.
	// Public names retain the existing overlay-first, external-resolver policy.
	resolveOverlay := !ip.IsValid()
	resolvePublic := resolveOverlay && !easytier.IsMagicDNS(host, e.zone)
	ticker := time.NewTicker(easyTierRoutePoll)
	defer ticker.Stop()
	for {
		if err := ctx.Err(); err != nil {
			return nil, netip.Addr{}, fmt.Errorf("easytier: wait for route to %s: %w", host, icontext.TCPDialError(ctx, err))
		}
		e.mu.Lock()
		instance, changed, closed := e.instance, e.readyCh, e.closed
		e.mu.Unlock()
		if closed || e.ctx.Err() != nil {
			return nil, netip.Addr{}, errEasyTierClosed
		}
		if instance != nil && instance.State() == corehost.StateRunning {
			snapshot, err := e.routeSnapshot(ctx, instance)
			if errors.Is(err, errEasyTierClosed) {
				return nil, netip.Addr{}, err
			}
			if err == nil {
				if resolveOverlay {
					// DHCP or a restarted instance can change a name's address
					// while its first route is still converging. Follow each
					// snapshot instead of waiting forever for the original IP.
					ip, _ = easytier.LookupOverlayHost(host, e.zone, snapshot.nodes())
					if ip.IsValid() {
						resolvePublic = false
					} else if resolvePublic {
						ip, err = resolvePublicIPv4(ctx, host)
						if err != nil {
							return nil, netip.Addr{}, err
						}
						resolveOverlay = false
					}
				}
				if ip.IsValid() && snapshot.readyFor(ip, e.option) {
					e.mu.Lock()
					current := e.instance == instance && !e.closed
					e.mu.Unlock()
					if current {
						return instance, ip, nil
					}
				}
			}
		}
		// Events are best-effort and route propagation need not emit peer_added.
		// Polling both covers dropped events and lets a running instance recover.
		select {
		case <-ctx.Done():
		case <-e.ctx.Done():
			return nil, netip.Addr{}, errEasyTierClosed
		case <-changed:
		case <-ticker.C:
		}
	}
}

func (s *easyTierRouteSnapshot) nodes() []easytier.Node {
	nodes := make([]easytier.Node, 0, len(s.routes)+1)
	if s.info != nil {
		ip, _ := easytier.ParseNodeIPv4(s.info.GetIpv4Addr())
		nodes = append(nodes, easytier.Node{Hostname: s.info.GetHostname(), IPv4: ip})
	}
	for _, route := range s.routes {
		if address := route.GetIpv4Addr().GetAddress(); address != nil {
			nodes = append(nodes, easytier.Node{Hostname: route.GetHostname(), IPv4: easytier.IPv4FromUint32(address.GetAddr())})
		}
	}
	return nodes
}

func (s *easyTierRouteSnapshot) readyFor(ip netip.Addr, option EasyTierOption) bool {
	if !ip.Is4() {
		return false
	}
	local, err := easytier.ParseNodeIPv4(s.info.GetIpv4Addr())
	if err != nil || !local.Is4() || local.IsUnspecified() {
		return false // DHCP/runtime address is not ready.
	}
	if local == ip || containsEasyTierCIDR(s.info.GetProxyCidrs(), ip) {
		return true
	}
	if s.hasRoute(ip, option.LatencyFirst != nil && *option.LatencyFirst) {
		return true
	}
	// EasyTier only falls back to an exit node outside its virtual subnet.
	// Never mistake a ready exit node for the missing 10.x game-server route.
	localNetwork, err := netip.ParsePrefix(s.info.GetIpv4Addr())
	if err != nil || localNetwork.Contains(ip) {
		return false
	}
	for _, address := range option.ExitNodes {
		exit, err := netip.ParseAddr(address)
		if err == nil && exit.Is4() && s.hasRoute(exit, option.LatencyFirst != nil && *option.LatencyFirst) {
			return true
		}
	}
	return false
}

func (s *easyTierRouteSnapshot) hasRoute(ip netip.Addr, latencyFirst bool) bool {
	for _, route := range s.routes {
		address := route.GetIpv4Addr().GetAddress()
		matches := address != nil && easytier.IPv4FromUint32(address.GetAddr()) == ip
		if !matches && !containsEasyTierCIDR(route.GetProxyCidrs(), ip) {
			continue
		}
		hop := route.GetNextHopPeerId()
		if latencyFirst && route.NextHopPeerIdLatencyFirst != nil {
			hop = route.GetNextHopPeerIdLatencyFirst()
		}
		// The next hop can be a relay, including a foreign-network relay not
		// listed as a direct peer. ListPeer/target P2P would reject valid paths.
		if route.GetPeerId() == s.info.GetPeerId() || hop != 0 {
			return true
		}
	}
	return false
}

func containsEasyTierCIDR(cidrs []string, ip netip.Addr) bool {
	for _, cidr := range cidrs {
		if prefix, err := netip.ParsePrefix(cidr); err == nil && prefix.Contains(ip) {
			return true
		}
	}
	return false
}
