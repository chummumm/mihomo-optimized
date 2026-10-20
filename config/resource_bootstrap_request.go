package config

import (
	"context"
	"errors"
	"net"
	"net/netip"
	"net/url"
	"sync"
	"time"

	"github.com/metacubex/mihomo/adapter/outboundgroup"
	"github.com/metacubex/mihomo/component/proxydialer"
	"github.com/metacubex/mihomo/component/resource"
	C "github.com/metacubex/mihomo/constant"
)

// bootstrapRequest is one idempotent resource GET, including redirects/body.
// Its exclusions use proxy objects, not display names: separate providers may
// contain identically named nodes. Concurrent GETs never share failure state.
type bootstrapRequest struct {
	owner        *bootstrapOutbound
	root         C.Proxy
	metadata     *C.Metadata
	excluded     map[C.Proxy]bool
	selectors    map[*bootstrapGroup]C.Proxy
	leaf         C.Proxy
	path         []bootstrapChoice
	prepared     bool
	eligibility  map[C.Proxy]bool
	selectionCtx context.Context
}

type bootstrapChoice struct {
	group *bootstrapGroup
	child C.Proxy
}

func (b *bootstrapOutbound) NewRequest(ctx context.Context, name, address string) (resource.BootstrapRequest, error) {
	fromProvider := false
	if name == "" {
		name = b.policy.Proxy
		fromProvider = b.policy.ProxyProvider != ""
	}
	if name == "" {
		return nil, nil
	}
	return b.request(ctx, name, fromProvider, address)
}

func (b *bootstrapOutbound) request(ctx context.Context, name string, fromProvider bool, address string) (*bootstrapRequest, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	p, err := b.root(name, fromProvider)
	if err != nil {
		return nil, err
	}
	metadata := &C.Metadata{Type: C.INNER}
	if parsed, err := url.Parse(address); err == nil && parsed.Scheme != "" && parsed.Host != "" {
		address = parsed.Host
		if parsed.Port() == "" {
			port := "80"
			if parsed.Scheme == "https" {
				port = "443"
			}
			address = net.JoinHostPort(parsed.Hostname(), port)
		}
	}
	if err := metadata.SetRemoteAddress(address); err != nil {
		return nil, err
	}
	return &bootstrapRequest{owner: b, root: p, metadata: metadata, excluded: make(map[C.Proxy]bool), selectors: make(map[*bootstrapGroup]C.Proxy)}, nil
}

func (r *bootstrapRequest) Next(ctx context.Context) (C.Dialer, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if err := r.owner.ctx.Err(); err != nil {
		return nil, err
	}
	if !r.prepared {
		// Testing an unrelated endpoint is advisory, not a prerequisite for
		// downloading the actual resource. One budget covers the entire nested
		// probe tree and leaves at least 3/4 of the remaining time for GETs.
		limit := 5 * time.Second
		if deadline, ok := ctx.Deadline(); ok && time.Until(deadline)/4 < limit {
			limit = time.Until(deadline) / 4
		}
		probeCtx, cancel := context.WithTimeout(ctx, limit)
		err := r.owner.prepareProbes(probeCtx, r.root)
		cancel()
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		if r.owner.ctx.Err() != nil {
			return nil, r.owner.ctx.Err()
		}
		if err != nil && !errors.Is(err, context.DeadlineExceeded) && !errors.Is(err, context.Canceled) {
			return nil, err
		}
		r.prepared = true
	}
	r.owner.mu.Lock()
	defer r.owner.mu.Unlock()
	r.path = nil
	r.eligibility = make(map[C.Proxy]bool)
	r.selectionCtx = ctx
	r.leaf = r.choose(r.root)
	if r.leaf == nil {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		if r.owner.ctx.Err() != nil {
			return nil, r.owner.ctx.Err()
		}
		return nil, r.owner.failure("no_usable_proxy", r.root.Name(), nil)
	}
	return bootstrapAttempt{owner: r.owner, proxy: r.leaf}, nil
}

func (r *bootstrapRequest) selector(group *bootstrapGroup) C.Proxy {
	if child := r.selectors[group]; child != nil {
		return child
	}
	child := group.ProxyGroup.(outboundgroup.CandidateSelector).SelectFrom(group.Proxies(), r.metadata, false)
	r.selectors[group] = child
	return child
}

func (r *bootstrapRequest) available(p C.Proxy) (available bool) {
	if r.selectionCtx.Err() != nil || r.owner.ctx.Err() != nil {
		return false
	}
	if cached, ok := r.eligibility[p]; ok {
		return cached
	}
	defer func() { r.eligibility[p] = available }()
	if p == nil || p == r.owner.proxies[bootstrapEmpty] || r.excluded[p] {
		return false
	}
	if group, ok := p.Adapter().(*bootstrapGroup); ok {
		if p.Type() == C.Selector {
			return r.available(r.selector(group))
		}
		for _, child := range group.Proxies() {
			if r.available(child) {
				return true
			}
		}
		return false
	}
	return true
}

func (r *bootstrapRequest) choose(p C.Proxy) C.Proxy {
	if !r.available(p) {
		return nil
	}
	group, ok := p.Adapter().(*bootstrapGroup)
	if !ok {
		return p
	}
	var child C.Proxy
	if p.Type() == C.Selector {
		// Freeze the direct child, not a flattened leaf. A selected fallback may
		// try its own nodes, but cannot escape into another selector member.
		child = r.selector(group)
	} else {
		var candidates []C.Proxy
		for _, p := range group.Proxies() {
			if r.available(p) {
				candidates = append(candidates, p)
			}
		}
		if p.Type() == C.Fallback && group.preferred != nil {
			for _, candidate := range candidates {
				if candidate == group.preferred {
					child = candidate
					break
				}
			}
		}
		if child == nil {
			child = group.ProxyGroup.(outboundgroup.CandidateSelector).SelectFrom(candidates, r.metadata, true)
		}
	}
	if child == nil {
		return nil
	}
	r.path = append(r.path, bootstrapChoice{group, child})
	return r.choose(child)
}

func (r *bootstrapRequest) Failed() {
	if r.leaf != nil {
		r.excluded[r.leaf] = true
	}
}
func (r *bootstrapRequest) Succeeded() {
	r.owner.mu.Lock()
	defer r.owner.mu.Unlock()
	for _, choice := range r.path {
		if choice.group.Type() == C.Fallback {
			choice.group.preferred = choice.child
		}
	}
}

// prepareProbes is lazy and finite. Child/dependency groups finish first, so a
// nested check never holds a probe slot while waiting for another ten workers.
// A failed test URL informs normal policy, but is not a requirement that blocks
// trying the real resource URL. Cancellation, however, always stops startup.
func (b *bootstrapOutbound) prepareProbes(ctx context.Context, p C.Proxy) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	group, ok := p.Adapter().(*bootstrapGroup)
	if !ok {
		node := p.Adapter().(*bootstrapNode)
		if parent, _ := node.mapping["dialer-proxy"].(string); parent != "" {
			target, err := b.root(parent, false)
			if err != nil {
				return err
			}
			return b.prepareProbes(ctx, target)
		}
		return nil
	}
	if group.Type() == C.Selector {
		b.mu.Lock()
		child := group.ProxyGroup.(outboundgroup.CandidateSelector).SelectFrom(group.Proxies(), nil, false)
		b.mu.Unlock()
		if child != nil {
			return b.prepareProbes(ctx, child)
		}
		return nil
	}
	group.probeMu.Lock()
	if group.probeDone {
		group.probeMu.Unlock()
		return nil
	}
	if wait := group.probeWait; wait != nil {
		group.probeMu.Unlock()
		select {
		case <-wait:
			return b.prepareProbes(ctx, p)
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	wait := make(chan struct{})
	group.probeWait = wait
	group.probeMu.Unlock()
	defer func() {
		group.probeMu.Lock()
		// Best-effort only once per private group, including a spent probe
		// sub-budget; later resources must not repeat the same hung checks.
		group.probeDone = true
		group.probeWait = nil
		close(wait)
		group.probeMu.Unlock()
	}()
	candidates := group.Proxies()
	for _, child := range candidates {
		if child == b.proxies[bootstrapEmpty] {
			continue
		}
		if err := b.prepareProbes(ctx, child); err != nil {
			return err
		}
	}
	testURL, expected, timeout := outboundgroup.ProbeOptions(group.ProxyGroup)
	if testURL == "" {
		return nil
	}
	var workers sync.WaitGroup
	for _, child := range candidates {
		if child == b.proxies[bootstrapEmpty] {
			continue
		}
		select {
		case b.probeSlots <- struct{}{}:
		case <-ctx.Done():
			workers.Wait()
			return ctx.Err()
		}
		workers.Add(1)
		go func(proxy C.Proxy) {
			defer workers.Done()
			defer func() { <-b.probeSlots }()
			checkCtx, cancel := context.WithTimeout(ctx, timeout)
			defer cancel()
			_, _ = proxy.URLTest(checkCtx, testURL, expected)
		}(child)
	}
	workers.Wait()
	return ctx.Err()
}

type bootstrapAttempt struct {
	owner *bootstrapOutbound
	proxy C.Proxy
}

func (a bootstrapAttempt) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	if err := a.owner.ctx.Err(); err != nil {
		return nil, err
	}
	conn, err := proxydialer.New(a.proxy, false).DialContext(ctx, network, address)
	if err != nil {
		return nil, err
	}
	if err := a.owner.own(conn); err != nil {
		return nil, err
	}
	return conn, nil
}
func (a bootstrapAttempt) ListenPacket(ctx context.Context, network, address string, destination netip.AddrPort) (net.PacketConn, error) {
	if err := a.owner.ctx.Err(); err != nil {
		return nil, err
	}
	conn, err := proxydialer.New(a.proxy, false).ListenPacket(ctx, network, address, destination)
	if err != nil {
		return nil, err
	}
	if err := a.owner.own(conn); err != nil {
		return nil, err
	}
	return conn, nil
}

type bootstrapNamedDialer struct {
	owner        *bootstrapOutbound
	name         string
	fromProvider bool
}

func (d bootstrapNamedDialer) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	ctx, cancel := context.WithCancel(ctx)
	stop := context.AfterFunc(d.owner.ctx, cancel)
	defer func() { stop(); cancel() }()
	request, err := d.owner.request(ctx, d.name, d.fromProvider, address)
	if err != nil {
		return nil, err
	}
	for {
		attempt, nextErr := request.Next(ctx)
		if nextErr != nil {
			if ctx.Err() != nil {
				return nil, ctx.Err()
			}
			if err != nil {
				return nil, err
			}
			return nil, nextErr
		}
		conn, dialErr := attempt.DialContext(ctx, network, address)
		if dialErr == nil {
			return conn, nil
		}
		err = dialErr
		request.Failed()
	}
}
func (d bootstrapNamedDialer) ListenPacket(ctx context.Context, network, address string, destination netip.AddrPort) (net.PacketConn, error) {
	ctx, cancel := context.WithCancel(ctx)
	stop := context.AfterFunc(d.owner.ctx, cancel)
	defer func() { stop(); cancel() }()
	request, err := d.owner.request(ctx, d.name, d.fromProvider, destination.String())
	if err != nil {
		return nil, err
	}
	for {
		attempt, nextErr := request.Next(ctx)
		if nextErr != nil {
			if ctx.Err() != nil {
				return nil, ctx.Err()
			}
			if err != nil {
				return nil, err
			}
			return nil, nextErr
		}
		conn, dialErr := attempt.ListenPacket(ctx, network, address, destination)
		if dialErr == nil {
			return conn, nil
		}
		err = dialErr
		request.Failed()
	}
}
