package sing_tun

import (
	"errors"
	"testing"
	"time"

	tun "github.com/metacubex/sing-tun"
	"golang.org/x/sys/windows"
)

func TestStandaloneWindowsTunRetainsUpstreamAttemptTiming(t *testing.T) {
	type attempt struct {
		cost time.Duration
		err  error
	}
	unknownFailure := errors.New("driver initialization failed")
	for _, test := range []struct {
		name     string
		attempts []attempt
		wantErr  error
	}{
		{name: "immediate success", attempts: []attempt{{}}},
		{name: "slow success", attempts: []attempt{{cost: 4 * time.Second}}},
		{name: "fast name conflict", attempts: []attempt{{err: windows.ERROR_ALREADY_EXISTS}}, wantErr: windows.ERROR_ALREADY_EXISTS},
		{name: "fast transient does not retry", attempts: []attempt{{err: windows.ERROR_BUSY}}, wantErr: windows.ERROR_BUSY},
		{name: "fast permission failure", attempts: []attempt{{err: windows.ERROR_ACCESS_DENIED}}, wantErr: windows.ERROR_ACCESS_DENIED},
		{name: "below one second", attempts: []attempt{{cost: time.Second - time.Nanosecond, err: unknownFailure}}, wantErr: unknownFailure},
		{name: "one second name conflict then success", attempts: []attempt{{cost: time.Second, err: windows.ERROR_ALREADY_EXISTS}, {}}},
		{name: "slow error outside managed whitelist then success", attempts: []attempt{{cost: time.Second, err: unknownFailure}, {}}},
		{
			name: "at most three slow attempts",
			attempts: []attempt{
				{cost: time.Second, err: unknownFailure},
				{cost: time.Second, err: unknownFailure},
				{cost: time.Second, err: windows.ERROR_ALREADY_EXISTS},
			},
			wantErr: windows.ERROR_ALREADY_EXISTS,
		},
		{
			name: "upstream has no managed three second retry budget",
			attempts: []attempt{
				{cost: 4 * time.Second, err: unknownFailure},
				{cost: 4 * time.Second, err: unknownFailure},
				{},
			},
		},
		{
			name: "each attempt measures its own elapsed time",
			attempts: []attempt{
				{cost: time.Second, err: unknownFailure},
				{cost: time.Second - time.Nanosecond, err: windows.ERROR_BUSY},
			},
			wantErr: windows.ERROR_BUSY,
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			current := time.Unix(1, 0)
			calls := 0
			result := &tun.NativeTun{}
			device, err := createStandaloneWindowsTun(tun.Options{Name: "Mihomo"}, func(options tun.Options) (tun.Tun, error) {
				if options.Name != "Mihomo" {
					t.Fatalf("standalone retry changed the requested alias: %q", options.Name)
				}
				if calls >= len(test.attempts) {
					t.Fatalf("unexpected retry after attempt %d", calls)
				}
				outcome := test.attempts[calls]
				calls++
				current = current.Add(outcome.cost)
				if outcome.err != nil {
					return nil, outcome.err
				}
				return result, nil
			}, func() time.Time { return current })
			if calls != len(test.attempts) || !errors.Is(err, test.wantErr) {
				t.Fatalf("attempts=%d want=%d err=%v want=%v", calls, len(test.attempts), err, test.wantErr)
			}
			if test.wantErr == nil && device != result {
				t.Fatal("successful standalone creation lost its returned device")
			}
			if test.wantErr != nil && device != nil {
				t.Fatal("failed standalone creation returned a device")
			}
		})
	}
}

func TestDesktopWindowsTunTransientRetryRetainsSuccessfulDevice(t *testing.T) {
	current := time.Unix(1, 0)
	result := &tun.NativeTun{}
	calls, waits := 0, 0
	device, err := createWindowsTun(tun.Options{Name: "utun"}, func(options tun.Options) (tun.Tun, error) {
		if options.Name != "utun" {
			t.Fatalf("managed retry changed the requested alias: %q", options.Name)
		}
		calls++
		if calls == 1 {
			return nil, windows.ERROR_BUSY
		}
		if calls > 2 {
			t.Fatal("managed startup retried after success")
		}
		return result, nil
	}, func() time.Time { return current }, func(delay time.Duration) {
		waits++
		if delay != 200*time.Millisecond || waits != 1 {
			t.Fatalf("managed retry wait changed: delay=%s waits=%d", delay, waits)
		}
		current = current.Add(delay)
	})
	if err != nil || device != result || calls != 2 || waits != 1 {
		t.Fatalf("managed retry lost success: device=%v err=%v calls=%d waits=%d", device, err, calls, waits)
	}
}
