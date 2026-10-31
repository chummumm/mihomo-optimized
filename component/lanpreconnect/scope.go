package lanpreconnect

import (
	"net/netip"
	"sync/atomic"
)

type fakeIPPolicy struct{ contains func(netip.Addr) bool }

var currentFakeIPPolicy atomic.Pointer[fakeIPPolicy]

// SetFakeIPPolicy publishes the applied DNS enhancer's immutable range check.
// Background jobs can finish cancellation concurrently with DNS replacement;
// they must not read the resolver's mutable global interface during that swap.
// The supplied function must be safe for concurrent calls and retain its own
// enhancer instance, rather than reading a changing global on each invocation.
func SetFakeIPPolicy(contains func(netip.Addr) bool) {
	if contains == nil {
		currentFakeIPPolicy.Store(nil)
		return
	}
	currentFakeIPPolicy.Store(&fakeIPPolicy{contains: contains})
}

// IsLAN uses the actual numeric destination, never a node's server address or
// a DNS lookup performed solely for preconnection. Mapped IPv4 is normalized.
func IsLAN(ip netip.Addr) bool {
	ip = ip.Unmap()
	if !ip.IsPrivate() {
		return false
	}
	policy := currentFakeIPPolicy.Load()
	return policy == nil || !policy.contains(ip)
}
