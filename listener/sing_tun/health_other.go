//go:build !windows

package sing_tun

import (
	"errors"

	tun "github.com/metacubex/sing-tun"
)

func (l *Listener) captureDeviceHealth(tun.Options) error { return nil }

func (l *Listener) CheckDeviceIdentity() (bool, error) { return true, nil }

func (l *Listener) CheckRoutes() (bool, error) {
	return false, errors.New("desktop TUN route inspection is only supported on Windows")
}
