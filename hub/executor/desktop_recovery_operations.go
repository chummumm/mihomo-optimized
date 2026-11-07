package executor

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"runtime"
	"strconv"
	"sync"
	"time"

	"github.com/metacubex/mihomo/component/desktop"
	"github.com/metacubex/mihomo/listener"
	"github.com/metacubex/mihomo/log"
)

var (
	ErrRecoverySuperseded = errors.New("TUN configuration or device generation changed")
	ErrRecoveryRequest    = errors.New("invalid recovery request identifier")
	ErrRecoveryNotFound   = errors.New("recovery operation was not found")
)

const (
	desktopRecoveryLimit   = 30 * time.Second
	desktopRecoveryHistory = 64
)

type DesktopRecoveryRequest struct {
	RequestID        string `json:"request_id"`
	ConfigGeneration uint64 `json:"config_generation"`
	TunGeneration    uint64 `json:"tun_generation"`
}

// DesktopRecoveryOperation is an immutable copy for callers. Its target
// generations stay fixed even if a successful replacement creates a new TUN.
// A failed/needs_restart deadline result may still own a native call; admission
// remains closed until that call and its cancellation cleanup actually return.
type DesktopRecoveryOperation struct {
	ID               string             `json:"id"`
	RequestID        string             `json:"request_id"`
	CoreInstance     string             `json:"core_instance"`
	ConfigGeneration uint64             `json:"config_generation"`
	TunGeneration    uint64             `json:"tun_generation"`
	Phase            string             `json:"phase"`
	Status           string             `json:"status"`
	Tun              listener.TunHealth `json:"tun"`
	Error            string             `json:"error,omitempty"`
	StartedAt        string             `json:"started_at"`
	DeadlineAt       string             `json:"deadline_at"`
	FinishedAt       string             `json:"finished_at,omitempty"`
}

type desktopRecoveryWork struct {
	snapshot DesktopRecoveryOperation
	request  DesktopRecoveryRequest
	result   listener.TunObservation
	cancel   context.CancelFunc
	done     chan struct{}
	started  time.Time
}

type desktopRecoveryCoordinator struct {
	mu        sync.Mutex
	active    *desktopRecoveryWork
	latest    *desktopRecoveryWork
	records   map[string]*desktopRecoveryWork
	requests  map[string]string
	order     []string
	sequence  uint64
	limit     time.Duration
	observe   func() listener.TunObservation
	recover   func(context.Context) (DesktopRecovery, error)
	ready     func() bool
	supported bool
}

func newDesktopRecoveryCoordinator(limit time.Duration) *desktopRecoveryCoordinator {
	return &desktopRecoveryCoordinator{
		records: make(map[string]*desktopRecoveryWork), requests: make(map[string]string), limit: limit,
		observe: listener.CurrentTunObservation, recover: runDesktopRecovery,
		ready: desktop.Ready, supported: runtime.GOOS == "windows",
	}
}

var (
	desktopCoreInstance = newDesktopCoreInstance()
	desktopRecoveries   = newDesktopRecoveryCoordinator(desktopRecoveryLimit)
)

func newDesktopCoreInstance() string {
	var value [16]byte
	if _, err := rand.Read(value[:]); err == nil {
		return hex.EncodeToString(value[:])
	}
	// This is a lifetime identifier, not an authentication credential.
	return fmt.Sprintf("%d-%d", os.Getpid(), time.Now().UnixNano())
}

func DesktopCoreInstance() string { return desktopCoreInstance }

func (m *desktopRecoveryCoordinator) current(observation listener.TunObservation) *DesktopRecoveryOperation {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.active != nil {
		copy := m.active.snapshot
		return &copy
	}
	if m.latest == nil || !sameTunGeneration(m.latest.result, observation) {
		return nil
	}
	// A later observation can identify a new failure on the same intact device,
	// or prove a previously uncertain result healthy. Neither should be hidden
	// by an unrelated terminal operation. Specific IDs remain queryable.
	healthy := observation.Tun.State == "healthy" || observation.Tun.State == "disabled"
	succeeded := m.latest.snapshot.Phase == "succeeded"
	if !observation.Stale && healthy != succeeded {
		return nil
	}
	copy := m.latest.snapshot
	return &copy
}

func sameTunGeneration(a, b listener.TunObservation) bool {
	return a.ConfigGeneration == b.ConfigGeneration && a.TunGeneration == b.TunGeneration
}

func validDesktopRecoveryRequestID(id string) bool {
	if len(id) < 1 || len(id) > 128 {
		return false
	}
	for _, char := range id {
		if !(char >= 'a' && char <= 'z' || char >= 'A' && char <= 'Z' || char >= '0' && char <= '9' || char == '-' || char == '_') {
			return false
		}
	}
	return true
}

// StartDesktopRecovery accepts work independently of the HTTP request. Holding
// mux is transferred to exactly one worker: config apply, stop cleanup and
// another recovery cannot overlap its native resource ownership.
func StartDesktopRecovery(ctx context.Context, request DesktopRecoveryRequest) (DesktopRecoveryOperation, error) {
	return desktopRecoveries.start(ctx, request)
}

func (m *desktopRecoveryCoordinator) start(ctx context.Context, request DesktopRecoveryRequest) (DesktopRecoveryOperation, error) {
	if err := ctx.Err(); err != nil {
		return DesktopRecoveryOperation{}, err
	}
	if !validDesktopRecoveryRequestID(request.RequestID) {
		return DesktopRecoveryOperation{}, ErrRecoveryRequest
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if id, exists := m.requests[request.RequestID]; exists {
		work := m.records[id]
		if work.request != request {
			return DesktopRecoveryOperation{}, ErrRecoverySuperseded
		}
		return work.snapshot, nil
	}
	if m.active != nil || !mux.TryLock() {
		return DesktopRecoveryOperation{}, ErrRecoveryBusy
	}
	workerOwnsLock := false
	defer func() {
		if !workerOwnsLock {
			mux.Unlock()
		}
	}()
	if ShutdownRequested() {
		return DesktopRecoveryOperation{}, ErrShuttingDown
	}
	if !m.ready() {
		return DesktopRecoveryOperation{}, ErrCoreNotReady
	}
	if err := ctx.Err(); err != nil {
		return DesktopRecoveryOperation{}, err
	}
	observation := m.observe()
	if request.ConfigGeneration != observation.ConfigGeneration || request.TunGeneration != observation.TunGeneration {
		return DesktopRecoveryOperation{}, ErrRecoverySuperseded
	}
	started := time.Now()
	m.sequence++
	operation := DesktopRecoveryOperation{
		ID:        desktopCoreInstance + "-" + strconv.FormatUint(m.sequence, 10),
		RequestID: request.RequestID, CoreInstance: desktopCoreInstance,
		ConfigGeneration: request.ConfigGeneration, TunGeneration: request.TunGeneration,
		Phase: "running", Status: "running", Tun: observation.Tun,
		StartedAt:  started.UTC().Format(time.RFC3339Nano),
		DeadlineAt: started.Add(m.limit).UTC().Format(time.RFC3339Nano),
	}
	operationCtx, cancel := context.WithTimeout(context.Background(), m.limit)
	work := &desktopRecoveryWork{snapshot: operation, request: request, result: observation,
		cancel: cancel, done: make(chan struct{}), started: started}
	m.active, m.latest = work, work
	m.records[operation.ID], m.requests[request.RequestID] = work, operation.ID
	m.order = append(m.order, operation.ID)
	for len(m.order) > desktopRecoveryHistory {
		oldID := m.order[0]
		m.order = m.order[1:]
		delete(m.requests, m.records[oldID].request.RequestID)
		delete(m.records, oldID)
	}
	workerOwnsLock = true
	log.Infoln("[Desktop Recovery] start operation=%s pid=%d config_generation=%d tun_generation=%d identity=%s creation_count=%d", operation.ID, os.Getpid(), request.ConfigGeneration, request.TunGeneration, observation.Tun.Identity, observation.CreationCount)
	go m.run(operationCtx, work)
	return operation, nil
}

func (m *desktopRecoveryCoordinator) run(ctx context.Context, work *desktopRecoveryWork) {
	// The deadline publishes a restartable failure even if a Windows native
	// call ignores cancellation. It does not release mux or active ownership.
	deadline, _ := ctx.Deadline()
	timer := time.AfterFunc(time.Until(deadline), func() { m.expire(work) })
	result, err := m.recover(ctx)
	timer.Stop()
	work.cancel()
	m.mu.Lock()
	if work.snapshot.Phase == "running" {
		work.snapshot.Status, work.snapshot.Tun, work.snapshot.Error = result.Status, result.Tun, result.Error
		work.snapshot.Phase = "failed"
		if err != nil {
			work.snapshot.Error = err.Error()
			if errors.Is(err, context.DeadlineExceeded) {
				work.snapshot.Status = "needs_restart"
			} else if errors.Is(err, context.Canceled) || errors.Is(err, ErrShuttingDown) {
				work.snapshot.Phase = "cancelled"
			}
		} else if result.Status == "healthy" || result.Status == "repaired" {
			work.snapshot.Phase = "succeeded"
		}
		work.snapshot.FinishedAt = time.Now().UTC().Format(time.RFC3339Nano)
	}
	work.result = m.observe()
	// Release the lifecycle barrier before another operation can observe
	// active=nil. No coordinator lock is held across native work.
	mux.Unlock()
	m.active = nil
	finished := work.snapshot
	close(work.done)
	m.mu.Unlock()
	log.Infoln("[Desktop Recovery] finish operation=%s pid=%d phase=%s status=%s duration_ms=%d creation_count=%d error=%q", finished.ID, os.Getpid(), finished.Phase, finished.Status, time.Since(work.started).Milliseconds(), work.result.CreationCount, finished.Error)
}

func (m *desktopRecoveryCoordinator) expire(work *desktopRecoveryWork) {
	work.cancel()
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.active != work || work.snapshot.Phase != "running" {
		return
	}
	work.snapshot.Phase, work.snapshot.Status = "failed", "needs_restart"
	work.snapshot.Error = "TUN recovery deadline exceeded; native operation remains owned until cleanup completes"
	work.snapshot.FinishedAt = time.Now().UTC().Format(time.RFC3339Nano)
	log.Warnln("[Desktop Recovery] deadline operation=%s pid=%d; preserve native ownership until process stop or cleanup", work.snapshot.ID, os.Getpid())
}

func (m *desktopRecoveryCoordinator) cancel() {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.active != nil {
		m.active.cancel()
	}
}

func GetDesktopRecovery(id string) (DesktopRecoveryOperation, error) {
	return desktopRecoveries.get(id)
}

func (m *desktopRecoveryCoordinator) get(id string) (DesktopRecoveryOperation, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if work := m.records[id]; work != nil {
		return work.snapshot, nil
	}
	return DesktopRecoveryOperation{}, ErrRecoveryNotFound
}

func recoveryResult(operation DesktopRecoveryOperation) DesktopRecovery {
	return DesktopRecovery{Status: operation.Status, Tun: operation.Tun, Error: operation.Error}
}
