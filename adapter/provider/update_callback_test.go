package provider

import (
	"testing"
	"time"

	C "github.com/metacubex/mihomo/constant"
)

func TestProxyProviderUpdateCallbackReadsPublishedSnapshot(t *testing.T) {
	health := NewHealthCheck(nil, "", 0, 0, true, nil)
	defer health.close()
	provider := &baseProvider{name: "updated", healthCheck: health}
	done := make(chan struct{})
	provider.SetProxyUpdateCallback(func() {
		// These take the provider read lock. A callback under the write lock
		// would deadlock the same path used by the LAN refresh coordinator.
		if provider.Version() != 1 || provider.Count() != 1 {
			t.Error("callback observed an unpublished proxy snapshot")
		}
		close(done)
	})
	go provider.setProxies([]C.Proxy{nil})
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("proxy update callback blocked on the provider lock")
	}
	provider.SetProxyUpdateCallback(nil)
	provider.setProxies(nil)
	if provider.Version() != 2 || provider.Count() != 0 {
		t.Fatal("clearing the callback prevented later provider updates")
	}
}
