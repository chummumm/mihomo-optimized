package dns

import (
	"context"
	"testing"
	"time"
)

func TestDNSUpstreamPendingContinuationsDoNotShareState(t *testing.T) {
	r := NewResolverFromClient(speedCheckTestClient{})
	t.Cleanup(r.Close)
	const key = "same-unresolved-upstream-scope"
	initialSlots := len(cacheBackgroundSlots)
	var contexts []context.Context
	for i := 0; i < 2; i++ {
		ctx := r.cacheControl.Context(context.Background())
		ctx = withSpeedCheckDeadline(ctx, time.Minute)
		ctx = context.WithValue(ctx, dnsUpstreamRouteKey{}, &dnsUpstreamRoute{key: key, cacheable: false})
		ctx, cancel := context.WithCancelCause(ctx)
		t.Cleanup(func() { cancel(nil) })
		contexts = append(contexts, ctx)
		lease := r.cacheControl.beginResponseContinuation(ctx, key, cancel)
		if lease != nil {
			t.Cleanup(lease.Close)
			t.Errorf("pending query %d registered a shared continuation", i)
		}
	}
	for i, ctx := range contexts {
		if ctx.Err() != nil {
			t.Errorf("pending query %d was canceled by its sibling: %v", i, context.Cause(ctx))
		}
	}
	r.cacheControl.mu.Lock()
	registered := len(r.cacheControl.selections)
	r.cacheControl.mu.Unlock()
	if registered != 0 || len(cacheBackgroundSlots) != initialSlots {
		t.Fatalf("pending continuations changed shared state: registrations=%d slots=%d, want 0/%d", registered, len(cacheBackgroundSlots), initialSlots)
	}
}

func TestDNSUpstreamPendingPublisherPreservesExistingContinuation(t *testing.T) {
	r := NewResolverFromClient(speedCheckTestClient{})
	t.Cleanup(r.Close)
	query := routingTestQuery("pending-continuation.example", 1)
	const key = "shared-placeholder-key"
	active := withSpeedCheckDeadline(r.cacheControl.Context(context.Background()), time.Minute)
	active, cancelActive := context.WithCancelCause(active)
	t.Cleanup(func() { cancelActive(nil) })
	lease := r.cacheControl.beginResponseContinuation(active, key, cancelActive)
	if lease == nil {
		t.Fatal("could not establish existing continuation")
	}
	t.Cleanup(lease.Close)
	initialSlots := len(cacheBackgroundSlots)
	for i := 0; i < 2; i++ {
		ctx := context.WithValue(r.cacheControl.Context(context.Background()), dnsUpstreamRouteKey{}, &dnsUpstreamRoute{key: key, cacheable: false})
		ctx = withDNSAnswerLifetimes(withSpeedCheckDeadline(ctx, time.Minute))
		ctx, cancel := context.WithCancelCause(ctx)
		t.Cleanup(func() { cancel(nil) })
		published, released := false, false
		publisher := &dnsResponsePublisher{
			r: r, ctx: ctx, query: query, key: key, cancel: cancel,
			release: func() { released = true },
			publish: func(*dnsExchangeResult) bool { published = true; return true },
		}
		t.Cleanup(publisher.Close)
		if publisher.offer(cacheTestReply(query, 60)) || published || released {
			t.Fatalf("pending publisher %d handed off a replayable answer", i)
		}
		if ctx.Err() != nil || active.Err() != nil {
			t.Fatalf("pending publisher %d canceled another selection: pending=%v active=%v", i, ctx.Err(), active.Err())
		}
	}
	r.cacheControl.mu.Lock()
	preserved := len(r.cacheControl.selections) == 1 && r.cacheControl.selections[key] == lease
	r.cacheControl.mu.Unlock()
	if !preserved || len(cacheBackgroundSlots) != initialSlots {
		t.Fatal("pending publisher replaced existing work or consumed background admission")
	}
	if _, _, hit := r.cache.GetWithExpire(key); hit {
		t.Fatal("pending publisher wrote a shared answer")
	}
}
