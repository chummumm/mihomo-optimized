package resource

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"sync"
	"time"

	C "github.com/metacubex/mihomo/constant"
)

const (
	DefaultBootstrapTimeout        = 180 * time.Second
	BootstrapConnectTimeout        = 10 * time.Second
	BootstrapProviderTimeout       = 60 * time.Second
	BootstrapGeoTimeout            = 120 * time.Second
	MaxBootstrapSeconds      int64 = int64((1<<63 - 1) / time.Second)
)

type bootstrapKey struct{}
type bootstrapLimitKey struct{}
type initialResourceKey struct{}

type initialResource struct{ kind, name string }

// Bootstrap owns only the temporary download transport. It is shared by
// configuration parsing and synchronous provider initialization, never by the
// providers' background refresh contexts.
type Bootstrap struct {
	ctx      context.Context
	cancel   context.CancelFunc
	dialer   C.Dialer
	closer   io.Closer
	once     sync.Once
	closeErr error
	routes   BootstrapRoutes
}

// BootstrapRoutes resolves explicit provider overrides and the startup default
// in the same private graph. Each GET owns its retries; background refreshes do
// not carry this scope or its temporary adapters.
type BootstrapRoutes interface {
	NewRequest(ctx context.Context, proxy, address string) (BootstrapRequest, error)
}

type BootstrapRequest interface {
	Next(context.Context) (C.Dialer, error)
	Failed()
	Succeeded()
}

var activeBootstraps = struct {
	sync.Mutex
	scopes map[*Bootstrap]struct{}
}{scopes: make(map[*Bootstrap]struct{})}

// CancelBootstraps interrupts in-flight resource requests without waiting for
// protocol teardown or taking the executor's configuration lock.
func CancelBootstraps() {
	activeBootstraps.Lock()
	defer activeBootstraps.Unlock()
	for b := range activeBootstraps.scopes {
		b.cancel()
	}
}

// WithBootstrapTimeout supplies a supervisor's remaining budget. It can only
// shorten the YAML budget; callers must reject a non-positive explicit limit.
func WithBootstrapTimeout(ctx context.Context, limit time.Duration) context.Context {
	return context.WithValue(ctx, bootstrapLimitKey{}, limit)
}

func ParseBootstrapTimeout(value string) (time.Duration, error) {
	limit, err := time.ParseDuration(value)
	if err != nil || limit <= 0 {
		return 0, NewBootstrapError("invalid_timeout", "config", "timeout", err)
	}
	return limit, nil
}

func NewBootstrap(ctx context.Context, timeout time.Duration, dialer C.Dialer, closer io.Closer, routes ...BootstrapRoutes) *Bootstrap {
	if limit, ok := ctx.Value(bootstrapLimitKey{}).(time.Duration); ok && limit < timeout {
		timeout = limit
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	b := &Bootstrap{cancel: cancel, dialer: dialer, closer: closer}
	if len(routes) > 0 {
		b.routes = routes[0]
	}
	b.ctx = context.WithValue(ctx, bootstrapKey{}, b)
	activeBootstraps.Lock()
	activeBootstraps.scopes[b] = struct{}{}
	activeBootstraps.Unlock()
	return b
}

func (b *Bootstrap) Context() context.Context { return b.ctx }

func (b *Bootstrap) Close() error {
	b.once.Do(func() {
		b.cancel()
		activeBootstraps.Lock()
		delete(activeBootstraps.scopes, b)
		activeBootstraps.Unlock()
		if b.closer != nil {
			b.closeErr = b.closer.Close()
		}
	})
	return b.closeErr
}

func BootstrapEnabled(ctx context.Context) bool {
	_, ok := ctx.Value(bootstrapKey{}).(*Bootstrap)
	return ok
}

func BootstrapDialer(ctx context.Context) C.Dialer {
	if b, ok := ctx.Value(bootstrapKey{}).(*Bootstrap); ok {
		return b.dialer
	}
	return nil
}

func BootstrapRequestFor(ctx context.Context, proxy, address string) (BootstrapRequest, error) {
	if b, ok := ctx.Value(bootstrapKey{}).(*Bootstrap); ok && b.routes != nil {
		return b.routes.NewRequest(ctx, proxy, address)
	}
	return nil, nil
}

func WithInitialResource(ctx context.Context, kind, name string) context.Context {
	return context.WithValue(ctx, initialResourceKey{}, initialResource{kind, name})
}

func InitialDownloadTimeout(ctx context.Context, fallback time.Duration) time.Duration {
	if !BootstrapEnabled(ctx) {
		return fallback
	}
	if label, ok := ctx.Value(initialResourceKey{}).(initialResource); ok && label.kind == "geo" {
		return BootstrapGeoTimeout
	}
	return BootstrapProviderTimeout
}

// BootstrapError keeps URL query strings, credentials and protocol error text
// out of user-visible startup diagnostics while retaining the cause for tests
// and cancellation decisions.
type BootstrapError struct {
	Code     string
	Kind     string
	Resource string
	cause    error
}

func NewBootstrapError(code, kind, name string, cause error) error {
	runes := []rune(name)
	if len(runes) > 96 {
		name = string(runes[:96])
	}
	return &BootstrapError{Code: code, Kind: kind, Resource: name, cause: cause}
}

func (e *BootstrapError) Error() string {
	return fmt.Sprintf("[resource-bootstrap] code=%s kind=%s resource=%q", e.Code, e.Kind, e.Resource)
}
func (e *BootstrapError) Unwrap() error { return e.cause }

func BootstrapFailure(ctx context.Context, err error) error {
	if err == nil || !BootstrapEnabled(ctx) {
		return err
	}
	var existing *BootstrapError
	if errors.As(err, &existing) {
		return existing
	}
	label, ok := ctx.Value(initialResourceKey{}).(initialResource)
	if !ok {
		label = initialResource{"config", "initialization"}
	}
	code := "download_failed"
	var networkError net.Error
	switch {
	case errors.Is(err, context.DeadlineExceeded):
		code = "deadline_exceeded"
	case errors.Is(err, context.Canceled):
		code = "canceled"
	case errors.As(err, &networkError) && networkError.Timeout():
		code = "connection_timeout"
	}
	return NewBootstrapError(code, label.kind, label.name, err)
}
