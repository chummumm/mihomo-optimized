package sing_tun

import (
	"errors"
	"time"

	"github.com/metacubex/mihomo/constant/features"
	"github.com/metacubex/mihomo/log"

	tun "github.com/metacubex/sing-tun"
	"golang.org/x/sys/windows"
)

func tunNew(options tun.Options) (tunIf tun.Tun, err error) {
	return createWindowsTun(options, func(options tun.Options) (tun.Tun, error) {
		return tun.NewExclusive(options)
	}, time.Now, time.Sleep)
}

func retryableWindowsTunError(err error) bool {
	// Name conflicts and access/parameter errors cannot be repaired by reopening
	// an adapter. Only retry explicit transient driver errors, after cleanup.
	return errors.Is(err, windows.ERROR_BUSY) || errors.Is(err, windows.ERROR_RETRY) ||
		errors.Is(err, windows.ERROR_NOT_READY) || errors.Is(err, windows.ERROR_SHARING_VIOLATION) ||
		errors.Is(err, windows.ERROR_LOCK_VIOLATION)
}

func createWindowsTun(options tun.Options, create func(tun.Options) (tun.Tun, error), now func() time.Time, wait func(time.Duration)) (tun.Tun, error) {
	// Native driver calls cannot be forcibly cancelled in-process. This deadline
	// bounds further retries; it never launches a replacement over an in-flight call.
	deadline := now().Add(3 * time.Second)
	for attempt := 1; ; attempt++ {
		device, err := create(options)
		if err == nil {
			return device, nil
		}
		if attempt == 3 || !retryableWindowsTunError(err) {
			return nil, err
		}
		delay := time.Duration(attempt) * 200 * time.Millisecond
		if !now().Add(delay).Before(deadline) {
			return nil, err
		}
		log.Warnln("[TUN] transient adapter creation error: %v [retry %d/2]", err, attempt)
		wait(delay)
	}
}

func init() {
	tun.TunnelType = InterfaceName

	if features.WindowsMajorVersion < 10 {
		// to resolve "bind: The requested address is not valid in its context"
		EnforceBindInterface = true
	}
}
