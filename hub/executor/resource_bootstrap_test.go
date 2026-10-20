package executor

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/metacubex/mihomo/component/geodata"
	"github.com/metacubex/mihomo/component/resource"
	P "github.com/metacubex/mihomo/constant/provider"
)

type resourceBootstrapProvider struct {
	name    string
	initial func(context.Context) error
}

func (p *resourceBootstrapProvider) Name() string                             { return p.name }
func (*resourceBootstrapProvider) VehicleType() P.VehicleType                 { return P.HTTP }
func (*resourceBootstrapProvider) Type() P.ProviderType                       { return P.Rule }
func (p *resourceBootstrapProvider) Initial() error                           { return p.initial(context.Background()) }
func (p *resourceBootstrapProvider) InitialContext(ctx context.Context) error { return p.initial(ctx) }
func (*resourceBootstrapProvider) Update() error                              { return nil }

func TestResourceBootstrapProviderFailurePolicyAndSharedDeadline(t *testing.T) {
	scope := resource.NewBootstrap(context.Background(), time.Second, nil, nil)
	defer scope.Close()
	provider := &resourceBootstrapProvider{name: "ordinary", initial: func(context.Context) error { return errors.New("ordinary provider unavailable") }}
	if err := loadProviderContext(scope.Context(), map[string]*resourceBootstrapProvider{provider.name: provider}); err != nil {
		t.Fatalf("ordinary provider failure became fatal: %v", err)
	}
	limited := resource.NewBootstrap(context.Background(), 30*time.Millisecond, nil, nil)
	defer limited.Close()
	provider.initial = func(ctx context.Context) error { <-ctx.Done(); return ctx.Err() }
	err := loadProviderContext(limited.Context(), map[string]*resourceBootstrapProvider{provider.name: provider})
	if !errors.Is(err, context.DeadlineExceeded) || !strings.Contains(err.Error(), "[resource-bootstrap]") {
		t.Fatalf("whole startup deadline was ignored: %v", err)
	}
}

func TestResourceBootstrapParseAndApplyShareSupervisorBudget(t *testing.T) {
	preserveStopAdmission(t)
	ctx := resource.WithBootstrapTimeout(context.Background(), 40*time.Millisecond)
	cfg, err := ParseWithBytesContext(ctx, []byte("resource-bootstrap: {timeout: 180}\ndns: {enable: false}\nrules: ['MATCH,DIRECT']\n"))
	if err != nil {
		t.Fatal(err)
	}
	defer cfg.CloseResourceBootstrap()
	<-cfg.ResourceContext().Done()
	if err := ApplyConfig(cfg, true); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("Apply reset the already-consumed parse budget: %v", err)
	}
}

func TestResourceBootstrapShutdownCancelsRequests(t *testing.T) {
	preserveStopAdmission(t)
	scope := resource.NewBootstrap(context.Background(), time.Second, nil, nil)
	defer scope.Close()
	RequestShutdown()
	if !errors.Is(scope.Context().Err(), context.Canceled) {
		t.Fatal("shutdown did not cancel resource initialization")
	}
	if _, err := ParseWithBytesContext(context.Background(), []byte("rules: ['MATCH,DIRECT']")); !errors.Is(err, ErrShuttingDown) {
		t.Fatalf("shutdown admitted resource parsing: %v", err)
	}
}

func TestResourceBootstrapParseGateDeadlineHasSafeDiagnostic(t *testing.T) {
	preserveStopAdmission(t)
	entered, release, done := make(chan struct{}), make(chan struct{}), make(chan error, 1)
	go func() {
		done <- geodata.WithInitializationContext(context.Background(), nil, func() error {
			close(entered)
			<-release
			return nil
		})
	}()
	<-entered
	defer func() {
		close(release)
		if err := <-done; err != nil {
			t.Error(err)
		}
	}()
	ctx := resource.WithBootstrapTimeout(context.Background(), 25*time.Millisecond)
	_, err := ParseWithBytesContext(ctx, []byte("dns: {enable: false}\nrules: ['MATCH,DIRECT']\n"))
	if !errors.Is(err, context.DeadlineExceeded) || !strings.Contains(err.Error(), "[resource-bootstrap] code=deadline_exceeded kind=config") {
		t.Fatalf("deadline waiting for parser gate lost its structured diagnostic: %v", err)
	}
}
