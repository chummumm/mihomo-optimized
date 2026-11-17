package route

import (
	"sync"
	"testing"
	"time"

	"github.com/metacubex/mihomo/component/resolver"

	"github.com/metacubex/http"
	"github.com/metacubex/http/httptest"
)

type gatedCacheResolver struct {
	resolver.Resolver
	entered chan struct{}
	release chan struct{}
}

func (r *gatedCacheResolver) ClearCache() {
	close(r.entered)
	<-r.release
}

func TestFlushDNSCacheWaitsForInvalidation(t *testing.T) {
	previousDefault, previousSystem := resolver.DefaultResolver, resolver.SystemResolver
	defer func() {
		resolver.DefaultResolver, resolver.SystemResolver = previousDefault, previousSystem
	}()
	main := &gatedCacheResolver{entered: make(chan struct{}), release: make(chan struct{})}
	system := &gatedCacheResolver{entered: make(chan struct{}), release: make(chan struct{})}
	resolver.DefaultResolver, resolver.SystemResolver = main, system
	var releaseMain, releaseSystem sync.Once

	response := httptest.NewRecorder()
	done := make(chan struct{})
	defer func() {
		releaseMain.Do(func() { close(main.release) })
		releaseSystem.Do(func() { close(system.release) })
		select {
		case <-done:
		case <-time.After(time.Second):
			t.Error("flush handler did not stop after releasing the test caches")
		}
	}()
	go func() {
		cacheRouter().ServeHTTP(response, httptest.NewRequest(http.MethodPost, "/dns/flush", nil))
		close(done)
	}()
	for _, cache := range []*gatedCacheResolver{main, system} {
		select {
		case <-cache.entered:
		case <-done:
			t.Fatal("flush returned before starting cache invalidation")
		case <-time.After(time.Second):
			t.Fatal("flush did not start cache invalidation")
		}
		select {
		case <-done:
			t.Fatal("flush returned while cache invalidation was still pending")
		case <-time.After(20 * time.Millisecond):
		}
		if cache == main {
			releaseMain.Do(func() { close(main.release) })
		} else {
			releaseSystem.Do(func() { close(system.release) })
		}
	}
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("flush did not return after cache invalidation")
	}
	if response.Code != http.StatusNoContent {
		t.Fatalf("flush returned status %d, expected 204", response.Code)
	}
}
