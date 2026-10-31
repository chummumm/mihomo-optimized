package executor

import (
	"sync"

	"github.com/metacubex/mihomo/config"
	"github.com/metacubex/mihomo/tunnel"
)

var lanRefreshRequests = make(chan struct{}, 1)
var lanRefreshOnce sync.Once

func RequestLANPreconnectRefresh() {
	if ShutdownRequested() {
		return
	}
	select {
	case lanRefreshRequests <- struct{}{}:
	default:
	}
}

func bindLANPreconnectUpdates(cfg *config.Config) {
	snapshot := CurrentUpdateConfiguration()
	if snapshot == nil {
		return
	}
	lanRefreshOnce.Do(func() {
		go func() {
			for range lanRefreshRequests {
				_ = ApplyConfigPatch(func() error {
					current := CurrentUpdateConfiguration()
					if current != nil && current.Context.Err() == nil && tunnel.Status() == tunnel.Running {
						tunnel.RefreshLANPreconnect()
					}
					return nil
				})
			}
		}()
	})
	for _, provider := range cfg.Providers {
		if binding, ok := provider.(interface{ SetProxyUpdateCallback(func()) }); ok {
			binding.SetProxyUpdateCallback(func() {
				if snapshot.Context.Err() == nil && CurrentUpdateConfiguration() == snapshot {
					RequestLANPreconnectRefresh()
				}
			})
		}
	}
}
