package statistic

import (
	"encoding/json"
	"math"
	"sort"
	"sync"
	"testing"
	"time"
)

func TestSnapshotSamplingMonotonicMilliseconds(t *testing.T) {
	manager := &Manager{}
	lower := float64(time.Since(snapshotSampling.epoch)) / float64(time.Millisecond)
	previous := manager.Snapshot()
	upper := float64(time.Since(snapshotSampling.epoch)) / float64(time.Millisecond)
	if math.IsNaN(previous.SampledAt) || math.IsInf(previous.SampledAt, 0) || previous.SampledAt < lower || previous.SampledAt > upper {
		t.Fatalf("sample time %v is outside the process-monotonic millisecond interval [%v, %v]", previous.SampledAt, lower, upper)
	}
	if previous.Sequence == 0 {
		t.Fatal("the first published sequence must be positive")
	}
	for i := 0; i < 128; i++ {
		next := manager.Snapshot()
		if next.Sequence <= previous.Sequence || next.SampledAt < previous.SampledAt {
			t.Fatalf("sample ordering went backwards: (%d, %v) -> (%d, %v)", previous.Sequence, previous.SampledAt, next.Sequence, next.SampledAt)
		}
		previous = next
	}
	other := (&Manager{}).Snapshot()
	if other.Sequence <= previous.Sequence || other.SampledAt < previous.SampledAt {
		t.Fatal("a zero-value manager started a different sampling epoch or sequence")
	}
}

func TestSnapshotSamplingConcurrentSequenceAndTime(t *testing.T) {
	const workers = 16
	const perWorker = 32
	var managers [2]Manager
	var waiting sync.WaitGroup
	start := make(chan struct{})
	results := make(chan *Snapshot, workers*perWorker)
	for worker := 0; worker < workers; worker++ {
		waiting.Add(1)
		go func(manager *Manager) {
			defer waiting.Done()
			<-start
			for i := 0; i < perWorker; i++ {
				results <- manager.Snapshot()
			}
		}(&managers[worker%len(managers)])
	}
	close(start)
	waiting.Wait()
	close(results)

	samples := make([]*Snapshot, 0, workers*perWorker)
	for sample := range results {
		samples = append(samples, sample)
	}
	if len(samples) != workers*perWorker {
		t.Fatalf("received %d snapshots, want %d", len(samples), workers*perWorker)
	}
	// Completion order may differ across callers; the published sequence is the
	// sampling order and must agree with the process clock in every response.
	sort.Slice(samples, func(i, j int) bool { return samples[i].Sequence < samples[j].Sequence })
	for i, sample := range samples {
		if sample.Sequence == 0 || sample.SampledAt < 0 || math.IsNaN(sample.SampledAt) || math.IsInf(sample.SampledAt, 0) {
			t.Fatalf("invalid sampling metadata: sequence=%d sampledAt=%v", sample.Sequence, sample.SampledAt)
		}
		if i == 0 {
			continue
		}
		previous := samples[i-1]
		if sample.Sequence == previous.Sequence {
			t.Fatalf("concurrent callers reused sequence %d", sample.Sequence)
		}
		if sample.SampledAt < previous.SampledAt {
			t.Fatalf("sampling time disagrees with sequence order: (%d, %v) -> (%d, %v)", previous.Sequence, previous.SampledAt, sample.Sequence, sample.SampledAt)
		}
	}
}

func TestSnapshotSamplingSurvivesTrafficReset(t *testing.T) {
	manager := &Manager{}
	manager.PushUploaded(17)
	manager.PushDownloaded(29)
	before := manager.Snapshot()
	if before.UploadTotal != 17 || before.DownloadTotal != 29 {
		t.Fatalf("unexpected initial totals: %d/%d", before.UploadTotal, before.DownloadTotal)
	}
	manager.ResetStatistic()
	after := manager.Snapshot()
	if after.UploadTotal != 0 || after.DownloadTotal != 0 {
		t.Fatalf("traffic reset left totals %d/%d", after.UploadTotal, after.DownloadTotal)
	}
	if after.Sequence <= before.Sequence || after.SampledAt < before.SampledAt {
		t.Fatal("traffic reset restarted the process sampling clock or sequence")
	}
}

func TestSnapshotSamplingJSONContract(t *testing.T) {
	manager := &Manager{}
	manager.PushUploaded(17)
	manager.PushDownloaded(29)
	snapshot := manager.Snapshot()
	wire, err := json.Marshal(snapshot)
	if err != nil {
		t.Fatal(err)
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(wire, &fields); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"downloadTotal", "uploadTotal", "connections", "memory", "sampledAt", "sequence"} {
		if _, exists := fields[key]; !exists {
			t.Fatalf("snapshot JSON omitted %q: %s", key, wire)
		}
	}
	var decoded Snapshot
	if err := json.Unmarshal(wire, &decoded); err != nil {
		t.Fatal(err)
	}
	if decoded.SampledAt != snapshot.SampledAt || decoded.Sequence != snapshot.Sequence {
		t.Fatalf("sampling metadata changed on the wire: %s", wire)
	}
	// Clients that only know the original fields continue to decode a snapshot.
	var legacy struct {
		DownloadTotal int64          `json:"downloadTotal"`
		UploadTotal   int64          `json:"uploadTotal"`
		Connections   []*TrackerInfo `json:"connections"`
		Memory        uint64         `json:"memory"`
	}
	if err := json.Unmarshal(wire, &legacy); err != nil {
		t.Fatal(err)
	}
	if legacy.DownloadTotal != 29 || legacy.UploadTotal != 17 || legacy.Connections == nil || len(legacy.Connections) != 0 || legacy.Memory != snapshot.Memory {
		t.Fatalf("original snapshot fields changed: %s", wire)
	}

	// Preserve fractional milliseconds and integer sequence numbers independently
	// of the host clock resolution and the current process uptime.
	fixture := Snapshot{SampledAt: 1234.125, Sequence: 9876543210}
	wire, err = json.Marshal(&fixture)
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(wire, &decoded); err != nil {
		t.Fatal(err)
	}
	if decoded.SampledAt != fixture.SampledAt || decoded.Sequence != fixture.Sequence {
		t.Fatalf("sampling number types were not preserved: %s", wire)
	}
}
