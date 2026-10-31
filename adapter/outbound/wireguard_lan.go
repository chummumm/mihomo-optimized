package outbound

import (
	"context"
	"errors"
	"net/netip"
	"strconv"
	"strings"
	"time"

	amneziav3 "github.com/metacubex/amneziawg-go/device"
	amnezia "github.com/metacubex/amneziawg-go/device_v1"
	"github.com/metacubex/mihomo/component/lanpreconnect"
	"github.com/metacubex/wireguard-go/device"
)

// wireGuardLANPeer follows the immutable AllowedIPs installed by genIpcConf:
// longest prefix wins, and a later peer replaces an equal prefix in UAPI.
func (w *WireGuard) wireGuardLANPeer(target netip.Addr) string {
	target = target.Unmap()
	if !target.IsValid() {
		return ""
	}
	if len(w.option.Peers) == 0 {
		for _, local := range w.localPrefixes {
			if local.Addr().Is4() == target.Is4() {
				return w.option.PublicKey
			}
		}
		return ""
	}
	key, bits := "", -1
	for _, peer := range w.option.Peers {
		for _, value := range peer.AllowedIPs {
			prefix, err := netip.ParsePrefix(value)
			if err == nil && prefix.Contains(target) && prefix.Bits() >= bits {
				key, bits = peer.PublicKey, prefix.Bits()
			}
		}
	}
	return key
}

func (w *WireGuard) LANPreconnectKey(target netip.Addr) string {
	if !target.IsValid() {
		return "runtime"
	}
	return w.wireGuardLANPeer(target)
}

func (w *WireGuard) LANPreconnectPrefixes() []netip.Prefix {
	var prefixes []netip.Prefix
	for _, peer := range w.option.Peers {
		for _, value := range peer.AllowedIPs {
			if prefix, err := netip.ParsePrefix(value); err == nil {
				prefixes = append(prefixes, prefix.Masked())
			}
		}
	}
	return prefixes
}

type wireGuardLANControl interface {
	SendKeepalive()
}

type wireGuardLANControlTask struct {
	key  string
	done chan struct{}
	err  error
}

// The native control call may write through an existing UDP-over-TCP carrier.
// A caller's deadline must not get stuck inside that write. Keep at most one
// such call per instance; later generations join it rather than spawning more.
// Its lifetime belongs to the instance, which closes bind IO before the device.
func (w *WireGuard) runLANControl(ctx context.Context, key string, control func() error) error {
	for {
		if err := ctx.Err(); err != nil {
			return err
		}
		if err := w.runCtx.Err(); err != nil {
			return err
		}
		w.lanControlMu.Lock()
		task := w.lanControl
		if task == nil {
			task = &wireGuardLANControlTask{key: key, done: make(chan struct{})}
			w.lanControl = task
			go func() {
				err := w.runCtx.Err()
				if err == nil {
					err = control()
				}
				w.lanControlMu.Lock()
				task.err = err
				if w.lanControl == task {
					w.lanControl = nil
				}
				close(task.done)
				w.lanControlMu.Unlock()
			}()
		}
		w.lanControlMu.Unlock()
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-w.runCtx.Done():
			return w.runCtx.Err()
		case <-task.done:
			if task.key == key {
				return task.err
			}
		}
	}
}

func wireGuardControlPeer(dev wireguardGoDevice, publicKey string) (wireGuardLANControl, error) {
	switch d := dev.(type) {
	case *device.Device:
		var key device.NoisePublicKey
		if err := key.FromHex(publicKey); err != nil {
			return nil, err
		}
		if peer := d.LookupPeer(key); peer != nil {
			return peer, nil
		}
	case *amnezia.Device:
		var key amnezia.NoisePublicKey
		if err := key.FromHex(publicKey); err != nil {
			return nil, err
		}
		if peer := d.LookupPeer(key); peer != nil {
			return peer, nil
		}
	case *amneziav3.Device:
		var key amneziav3.NoisePublicKey
		if err := key.FromHex(publicKey); err != nil {
			return nil, err
		}
		if peer := d.LookupPeer(key); peer != nil {
			return peer, nil
		}
	}
	return nil, errors.New("wireguard: selected LAN peer is unavailable")
}

// UAPI is the existing read-only session-status API in all three implementations.
// Its response also contains key material: consume it locally, never log it.
func wireGuardRecentHandshake(dev wireguardGoDevice, publicKey string, now time.Time) bool {
	status, ok := dev.(interface{ IpcGet() (string, error) })
	if !ok {
		return false
	}
	data, err := status.IpcGet()
	if err != nil {
		return false
	}
	selected := false
	for _, line := range strings.Split(data, "\n") {
		key, value, ok := strings.Cut(line, "=")
		if !ok {
			continue
		}
		if key == "public_key" {
			selected = value == publicKey
			continue
		}
		if selected && key == "last_handshake_time_sec" {
			seconds, err := strconv.ParseInt(value, 10, 64)
			if err != nil || seconds <= 0 {
				return false
			}
			age := now.Sub(time.Unix(seconds, 0))
			return age >= 0 && age < 120*time.Second
		}
	}
	return false
}

func (w *WireGuard) PrepareLAN(ctx context.Context, target netip.Addr) error {
	if target.IsValid() && !lanpreconnect.IsLAN(target) {
		return errors.New("wireguard: LAN preparation requires a private target")
	}
	if err := w.init(ctx); err != nil {
		return err
	}
	var keys []string
	if target.IsValid() {
		key := w.wireGuardLANPeer(target)
		if key == "" {
			return errors.New("wireguard: no AllowedIPs route for LAN target")
		}
		keys = append(keys, key)
	} else if len(w.option.Peers) == 0 {
		keys = append(keys, w.option.PublicKey)
	} else {
		// A prefix-only intent cannot invent a host. Initialize the runtime;
		// per-target peer selection waits for an actual known address.
		return nil
	}
	w.deviceMutex.Lock()
	dev := w.device
	w.deviceMutex.Unlock()
	for _, key := range keys {
		if err := ctx.Err(); err != nil {
			return err
		}
		err := w.runLANControl(ctx, key, func() error {
			// tun.Start emits an asynchronous EventUp. Up is idempotent and
			// serialized; confirm Bind.Open before an explicit control packet.
			if native, ok := dev.(interface{ Up() error }); ok {
				if err := native.Up(); err != nil {
					return err
				}
			}
			peer, err := wireGuardControlPeer(dev, key)
			if err != nil {
				return err
			}
			// This native empty keepalive also initiates a missing handshake.
			peer.SendKeepalive()
			return nil
		})
		if err != nil {
			return err
		}
		if wireGuardRecentHandshake(dev, key, time.Now()) {
			continue
		}
		ticker := time.NewTicker(100 * time.Millisecond)
		for !wireGuardRecentHandshake(dev, key, time.Now()) {
			select {
			case <-ctx.Done():
				ticker.Stop()
				return ctx.Err()
			case <-w.runCtx.Done():
				ticker.Stop()
				return w.runCtx.Err()
			case <-ticker.C:
			}
		}
		ticker.Stop()
	}
	return nil
}
