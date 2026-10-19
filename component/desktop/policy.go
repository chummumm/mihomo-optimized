// Package desktop holds the immutable process policy selected by a desktop
// supervisor. It has no YAML surface: reloading a proxy configuration cannot
// transfer process ownership or broaden the supervisor's control channel.
package desktop

import (
	"errors"
	"fmt"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"unicode"
)

var ErrManaged = errors.New("desktop_managed: the desktop supervisor owns core and UI updates and process restart")

type Options struct {
	Home       string
	Controller string
	Secret     string
}

type policy struct {
	Options
	shutdown chan struct{}
	once     sync.Once
	ready    atomic.Bool
}

var current atomic.Pointer[policy]

// Enable is called once, before configuration parsing, filesystem initialization
// or networking. The supervisor creates the home with its intended ACL and
// supplies a secret generated from at least 32 random bytes through the child
// environment. Secrets are never persisted, logged, or returned by the API.
func Enable(options Options) error {
	validated, err := validateOptions(options)
	if err != nil {
		return err
	}
	p := &policy{Options: validated, shutdown: make(chan struct{})}
	if !current.CompareAndSwap(nil, p) {
		return errors.New("desktop-managed policy is already initialized")
	}
	return nil
}

func validateOptions(options Options) (Options, error) {
	if !filepath.IsAbs(options.Home) {
		return Options{}, errors.New("desktop-managed requires an absolute configuration home (-d)")
	}
	home := filepath.Clean(options.Home)
	info, err := os.Lstat(home)
	if err != nil || !info.IsDir() || isLink(info) {
		return Options{}, errors.New("desktop-managed home must be an existing directory without a symbolic link or reparse point")
	}
	home, err = filepath.EvalSymlinks(home)
	if err != nil {
		return Options{}, fmt.Errorf("resolve desktop-managed home: %w", err)
	}
	if err := checkHomeDirectories(home); err != nil {
		return Options{}, err
	}
	host, port, err := net.SplitHostPort(options.Controller)
	if err != nil {
		return Options{}, errors.New("desktop-managed requires an explicit loopback controller address and port (-ext-ctl)")
	}
	address, err := netip.ParseAddr(host)
	if err != nil || !address.IsLoopback() || address.Zone() != "" {
		return Options{}, errors.New("desktop-managed controller must use a loopback IP literal")
	}
	number, err := strconv.ParseUint(port, 10, 16)
	if err != nil || number == 0 {
		return Options{}, errors.New("desktop-managed controller port must be 1..65535")
	}
	if len(options.Secret) < 32 || len(options.Secret) > 256 || strings.IndexFunc(options.Secret, func(r rune) bool {
		return r > unicode.MaxASCII || unicode.IsSpace(r) || unicode.IsControl(r)
	}) >= 0 {
		return Options{}, errors.New("desktop-managed requires a supervisor-generated random secret of 32..256 ASCII characters through CLASH_OVERRIDE_SECRET")
	}
	options.Home = home
	options.Controller = net.JoinHostPort(address.String(), strconv.FormatUint(number, 10))
	return options, nil
}

func Enabled() bool { return current.Load() != nil }

// Ready is published only after a full configuration application succeeds.
// The tunnel's traffic phase can become Running before listener errors are
// collected, so it cannot establish supervisor readiness on its own.
func Ready() bool {
	p := current.Load()
	return p != nil && p.ready.Load()
}

// SetReady is called by the serialized configuration executor, after admission
// checks and at the end of the apply transaction. It is a no-op in standalone
// mode and never turns a failed reload into an implicit rollback.
func SetReady(ready bool) {
	if p := current.Load(); p != nil {
		p.ready.Store(ready)
	}
}

func AllowedOrigins() []string {
	return []string{"tauri://localhost", "http://tauri.localhost", "https://tauri.localhost", "http://localhost:3000"}
}

func Home() string {
	if p := current.Load(); p != nil {
		return p.Home
	}
	return ""
}

func Controller() (address, secret string) {
	if p := current.Load(); p != nil {
		return p.Controller, p.Secret
	}
	return "", ""
}

// A nil channel leaves the standalone daemon's signal handling unchanged.
func ShutdownRequests() <-chan struct{} {
	if p := current.Load(); p != nil {
		return p.shutdown
	}
	return nil
}

func RequestShutdown() {
	if p := current.Load(); p != nil {
		p.once.Do(func() { close(p.shutdown) })
	}
}

// CheckPath confines all managed data to the supervisor-selected home. Unlike
// the standalone SAFE_PATHS compatibility policy, it rejects symlink/reparse
// components, including an existing destination, and cannot be bypassed by
// environment configuration. Call again at the actual read/write boundary.
// The supervisor must protect home/parent directory ACLs from other users;
// this check is not a replacement for those operating-system access controls.
func CheckPath(path string) error {
	p := current.Load()
	if p == nil {
		return nil
	}
	return checkPath(p.Home, path)
}

func checkPath(home, path string) error {
	if !filepath.IsAbs(path) {
		return errors.New("desktop-managed I/O requires an absolute path")
	}
	path = filepath.Clean(path)
	relative, err := filepath.Rel(home, path)
	if err != nil || !filepath.IsLocal(relative) {
		return errors.New("desktop-managed paths must stay inside the configuration home")
	}
	// Replacing home or one of its parents must not redirect later writes.
	if err := checkHomeDirectories(home); err != nil {
		return err
	}
	parts := strings.Split(relative, string(filepath.Separator))
	checked := home
	for i, part := range parts {
		if part == "." {
			continue
		}
		// In particular reject Windows alternate data streams, device aliases,
		// and trailing-dot/space aliases even on a cross-platform validator.
		if strings.ContainsAny(part, ":\x00") || strings.TrimRight(part, ". ") != part {
			return errors.New("desktop-managed path contains an unsafe filename")
		}
		checked = filepath.Join(checked, part)
		info, err := os.Lstat(checked)
		if os.IsNotExist(err) {
			continue
		}
		if err != nil {
			return fmt.Errorf("inspect desktop-managed path: %w", err)
		}
		if isLink(info) || i < len(parts)-1 && !info.IsDir() || i == len(parts)-1 && !info.IsDir() && !info.Mode().IsRegular() {
			return errors.New("desktop-managed paths cannot contain symbolic links, reparse points or special files")
		}
	}
	return nil
}

// home is canonicalized at bootstrap. Checking its full parent chain on every
// I/O also detects a subsequently substituted directory junction above home.
func checkHomeDirectories(home string) error {
	for checked := home; ; {
		info, err := os.Lstat(checked)
		if err != nil || !info.IsDir() || isLink(info) {
			return errors.New("desktop-managed configuration home or parent is no longer a safe directory")
		}
		parent := filepath.Dir(checked)
		if parent == checked {
			return nil
		}
		checked = parent
	}
}
