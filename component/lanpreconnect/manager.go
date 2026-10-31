// Package lanpreconnect schedules bounded, best-effort control-plane work for
// already selected LAN outbounds. It never owns business packets or deadlines.
package lanpreconnect

import (
	"context"
	"net/netip"
	"sync"
	"time"

	"github.com/metacubex/mihomo/component/resolver"
	"github.com/metacubex/mihomo/log"
)

const (
	maxIntents       = 128
	workers          = 2
	prepareTimeout   = 15 * time.Second
	recentLifetime   = 30 * time.Minute
	preparedLifetime = time.Minute
)

// Preparer is an instance, not a proxy group. An invalid target requests only
// runtime/control-session startup for a proven LAN prefix. A valid target is an
// actual destination; implementations must not probe its business ports.
// Implementations are pointer values and must honor cancellation or attach any
// continuing initialization to their own bounded instance lifecycle.
type Preparer interface {
	Name() string
	PrepareLAN(context.Context, netip.Addr) error
}

// PathKeyer optionally coalesces destinations that provably use the same path.
// It must be a cheap, side-effect-free lookup; unknown paths return an empty key.
type PathKeyer interface{ LANPreconnectKey(netip.Addr) string }

type Intent struct {
	Outbound Preparer
	Target   netip.Addr
}

type intentKey struct {
	outbound Preparer
	target   netip.Addr
}
type pathKey struct {
	outbound Preparer
	path     string
}
type entry struct {
	key        intentKey
	static     bool
	used       time.Time
	preparedAt time.Time
	next       time.Time
	pending    bool
	attempts   int
	generation uint64
	cancel     context.CancelFunc
}
type job struct {
	entry             *entry
	generation, token uint64
	ctx               context.Context
	cancel            context.CancelFunc
	path              pathKey
}

// Manager bounds both memory and active work. Only one job per instance runs
// at once; a canceled generation keeps its worker slot until it has exited.
type Manager struct {
	mu                sync.Mutex
	ctx               context.Context
	cancel            context.CancelFunc
	wake              chan struct{}
	wg                sync.WaitGroup
	enabled           bool
	entries           map[intentKey]*entry
	busy              map[Preparer]uint64
	prepared          map[pathKey]time.Time
	sequence          uint64
	lastNetworkChange time.Time
}

func New() *Manager {
	ctx, cancel := context.WithCancel(context.Background())
	m := &Manager{ctx: ctx, cancel: cancel, wake: make(chan struct{}, workers),
		entries: make(map[intentKey]*entry), busy: make(map[Preparer]uint64), prepared: make(map[pathKey]time.Time)}
	for i := 0; i < workers; i++ {
		m.wg.Add(1)
		go m.run()
	}
	return m
}

var Default = New()

// IsLAN uses the actual numeric destination, never a node's server address or
// a DNS lookup performed solely for preconnection. Mapped IPv4 is normalized.
func IsLAN(ip netip.Addr) bool {
	ip = ip.Unmap()
	return ip.IsPrivate() && !resolver.IsFakeIP(ip)
}

// Replace applies a completed configuration generation. No parsing/check path
// should call it. Old work is canceled before the new intents become runnable.
func (m *Manager) Replace(intents []Intent) {
	m.mu.Lock()
	if m.ctx.Err() != nil {
		m.mu.Unlock()
		return
	}
	m.clearLocked()
	m.enabled = true
	for _, intent := range intents {
		if intent.Outbound == nil || (intent.Target.IsValid() && !IsLAN(intent.Target)) {
			continue
		}
		m.addLocked(intent.Outbound, intent.Target.Unmap(), true)
	}
	m.mu.Unlock()
	m.notify()
}

// Observe records a real numeric LAN destination without waiting for work.
// It does not extend a request, transmit data, or replay an unsuccessful packet.
func (m *Manager) Observe(outbound Preparer, target netip.Addr) {
	if outbound == nil || !IsLAN(target) {
		return
	}
	m.mu.Lock()
	if m.enabled && m.ctx.Err() == nil {
		m.addLocked(outbound, target.Unmap(), false)
	}
	m.mu.Unlock()
	m.notify()
}

func (m *Manager) addLocked(outbound Preparer, target netip.Addr, static bool) {
	now := time.Now()
	key := intentKey{outbound, target}
	if e := m.entries[key]; e != nil {
		e.used, e.static = now, e.static || static
		// A fresh real request may retry a previously exhausted cold path, but
		// repeated packets never create concurrent jobs or reset the backoff.
		if !e.pending && e.attempts != 0 && !now.Before(e.next) {
			e.pending = true
			e.attempts = 0
		}
		if !e.pending && e.attempts == 0 && now.Sub(e.preparedAt) >= preparedLifetime {
			e.pending, e.next = true, now
		}
		return
	}
	if len(m.entries) >= maxIntents {
		var oldest *entry
		for _, e := range m.entries {
			if e.static {
				continue
			}
			if oldest == nil || e.used.Before(oldest.used) {
				oldest = e
			}
		}
		if oldest == nil {
			return
		}
		m.removeLocked(oldest)
	}
	m.sequence++
	m.entries[key] = &entry{key: key, used: now, next: now, pending: true, static: static, generation: m.sequence}
}

func (m *Manager) removeLocked(e *entry) {
	if e.cancel != nil {
		e.cancel()
	}
	delete(m.entries, e.key)
	// The cache is bounded by intents, not by every peer ever encountered.
	for key := range m.prepared {
		if key.outbound == e.key.outbound {
			delete(m.prepared, key)
		}
	}
}

func (m *Manager) clearLocked() {
	for _, e := range m.entries {
		if e.cancel != nil {
			e.cancel()
		}
	}
	m.entries = make(map[intentKey]*entry)
	m.prepared = make(map[pathKey]time.Time)
}

func (m *Manager) Suspend() {
	m.mu.Lock()
	m.enabled = false
	m.clearLocked()
	m.mu.Unlock()
	m.notify()
}

func (m *Manager) Forget(outbound Preparer) {
	m.mu.Lock()
	for _, e := range m.entries {
		if e.key.outbound == outbound {
			m.removeLocked(e)
		}
	}
	m.mu.Unlock()
	m.notify()
}

// Changed invalidates an instance's old route results; it does not start an
// instance with no static or recently observed LAN intent.
func (m *Manager) Changed(outbound Preparer) { m.invalidate(outbound, false) }
func (m *Manager) NetworkChanged()           { m.invalidate(nil, true) }
func NetworkChanged()                        { Default.NetworkChanged() }

func (m *Manager) invalidate(outbound Preparer, network bool) {
	m.mu.Lock()
	now := time.Now()
	if network && now.Sub(m.lastNetworkChange) < 250*time.Millisecond {
		m.mu.Unlock()
		return
	}
	if network {
		m.lastNetworkChange = now
	}
	for _, e := range m.entries {
		if !e.static && now.Sub(e.used) > recentLifetime {
			m.removeLocked(e)
			continue
		}
		if outbound != nil && e.key.outbound != outbound {
			continue
		}
		if e.cancel != nil {
			e.cancel()
		}
		m.sequence++
		e.generation, e.pending, e.attempts, e.next = m.sequence, true, 0, now
	}
	for key := range m.prepared {
		if outbound == nil || key.outbound == outbound {
			delete(m.prepared, key)
		}
	}
	m.mu.Unlock()
	m.notify()
}

func (m *Manager) notify() {
	for i := 0; i < workers; i++ {
		select {
		case m.wake <- struct{}{}:
		default:
		}
	}
}

func (m *Manager) nextJob() (*job, time.Duration) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delay := time.Minute
	if !m.enabled {
		return nil, delay
	}
	now := time.Now()
	for key, preparedAt := range m.prepared {
		if now.Sub(preparedAt) >= preparedLifetime {
			delete(m.prepared, key)
		}
	}
	for _, e := range m.entries {
		if !e.static && now.Sub(e.used) > recentLifetime {
			m.removeLocked(e)
			continue
		}
		if !e.pending || m.busy[e.key.outbound] != 0 {
			continue
		}
		if wait := e.next.Sub(now); wait > 0 {
			if wait < delay {
				delay = wait
			}
			continue
		}
		path := pathKey{outbound: e.key.outbound}
		if source, ok := e.key.outbound.(PathKeyer); ok {
			path.path = source.LANPreconnectKey(e.key.target)
		}
		if path.path != "" {
			if preparedAt, ok := m.prepared[path]; ok && now.Sub(preparedAt) < preparedLifetime {
				e.pending = false
				e.preparedAt = preparedAt
				continue
			}
		}
		m.sequence++
		ctx, cancel := context.WithTimeout(m.ctx, prepareTimeout)
		e.cancel = cancel
		m.busy[e.key.outbound] = m.sequence
		return &job{e, e.generation, m.sequence, ctx, cancel, path}, delay
	}
	return nil, delay
}

func (m *Manager) finish(j *job, err error) {
	j.cancel()
	m.mu.Lock()
	if m.busy[j.entry.key.outbound] == j.token {
		delete(m.busy, j.entry.key.outbound)
	}
	e := m.entries[j.entry.key]
	if e == j.entry && e.generation == j.generation {
		e.cancel = nil
		if err == nil {
			e.pending, e.attempts = false, 0
			e.preparedAt = time.Now()
			if j.path.path != "" {
				m.prepared[j.path] = e.preparedAt
			}
		} else {
			delays := [...]time.Duration{5 * time.Second, 15 * time.Second, 30 * time.Second}
			e.attempts++
			index := e.attempts - 1
			if index >= len(delays) {
				index = len(delays) - 1
				e.pending = false
			}
			// Per-job jitter keeps different instances from retrying in lockstep.
			e.next = time.Now().Add(delays[index] + time.Duration(j.token%501)*time.Millisecond)
			log.Debugln("[LAN] background preparation for %s: %v", e.key.outbound.Name(), err)
		}
	}
	m.mu.Unlock()
	m.notify()
}

func (m *Manager) run() {
	defer m.wg.Done()
	for m.ctx.Err() == nil {
		j, delay := m.nextJob()
		if j != nil {
			m.finish(j, j.entry.key.outbound.PrepareLAN(j.ctx, j.entry.key.target))
			continue
		}
		timer := time.NewTimer(delay)
		select {
		case <-m.ctx.Done():
			timer.Stop()
			return
		case <-m.wake:
			timer.Stop()
		case <-timer.C:
		}
	}
}

// Stop is a nonblocking permanent shutdown barrier. A late configuration apply
// cannot revive this manager; active jobs retain their worker slots until exit.
func (m *Manager) Stop() {
	m.mu.Lock()
	m.cancel()
	m.enabled = false
	m.clearLocked()
	m.mu.Unlock()
	m.notify()
}
func (m *Manager) Close() { m.Stop(); m.wg.Wait() }

type businessKey struct{}
type selectedKey struct{}
type selection struct{ outbound Preparer }

// Business marks the tunnel's business request. Nested endpoint/DNS dials may
// inherit it, but Select seals it at the first actual leaf, even a non-L3 leaf.
func Business(ctx context.Context) context.Context {
	return context.WithValue(ctx, businessKey{}, true)
}
func NeedsSelection(ctx context.Context) bool {
	if ctx.Value(businessKey{}) != true {
		return false
	}
	_, selected := ctx.Value(selectedKey{}).(selection)
	return !selected
}
func Select(ctx context.Context, outbound Preparer) context.Context {
	if ctx.Value(businessKey{}) != true {
		return ctx
	}
	if _, ok := ctx.Value(selectedKey{}).(selection); ok {
		return ctx
	}
	return context.WithValue(ctx, selectedKey{}, selection{outbound})
}
func Selected(ctx context.Context, outbound Preparer) bool {
	selected, ok := ctx.Value(selectedKey{}).(selection)
	return ok && selected.outbound != nil && selected.outbound == outbound
}
func Observe(ctx context.Context, outbound Preparer, target netip.Addr) {
	if Selected(ctx, outbound) {
		Default.Observe(outbound, target)
	}
}
