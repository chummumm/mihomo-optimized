package desktop

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"strconv"
)

const (
	LaunchNonceEnv           = "MIHOMO_DESKTOP_LAUNCH_NONCE"
	ControllerRecordName     = "desktop-controller.json"
	ControllerRecordVersion  = 1
	ControllerRecordMaxBytes = 4096
)

// ControllerRecord identifies one listener held by this process. It contains
// no API credential. The supervisor must still authenticate /desktop and match
// the retained child PID, launch nonce and core instance before using it.
type ControllerRecord struct {
	ProtocolVersion int    `json:"protocolVersion"`
	PID             int    `json:"pid"`
	Controller      string `json:"controller"`
	CoreInstance    string `json:"coreInstance"`
	LaunchNonce     string `json:"launchNonce"`
}

func validLaunchNonce(value string) bool {
	if len(value) != 64 {
		return false
	}
	for _, c := range value {
		if !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f') {
			return false
		}
	}
	return true
}

func validCoreInstance(value string) bool {
	if len(value) == 0 || len(value) > 128 {
		return false
	}
	for _, c := range value {
		if c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' {
			continue
		}
		return false
	}
	return true
}

// PublishController is called only after net.Listen succeeds, never by Enable
// or configuration validation. The address must belong to the immutable
// process policy; a reload cannot transfer ownership to another endpoint.
func PublishController(address, instance string) error {
	p := current.Load()
	if p == nil {
		return errors.New("desktop controller publication requires managed mode")
	}
	p.controllerMu.Lock()
	defer p.controllerMu.Unlock()

	host, port, err := net.SplitHostPort(address)
	if err != nil {
		return errors.New("desktop controller did not bind a valid address")
	}
	ip, err := netip.ParseAddr(host)
	if err != nil || !ip.IsLoopback() || ip.Zone() != "" {
		return errors.New("desktop controller did not bind a loopback IP literal")
	}
	number, err := strconv.ParseUint(port, 10, 16)
	if err != nil || number == 0 {
		return errors.New("desktop controller did not bind a nonzero port")
	}
	expectedHost, expectedPort, _ := net.SplitHostPort(p.Options.Controller)
	if ip.String() != expectedHost || expectedPort != "0" && port != expectedPort {
		return errors.New("desktop controller bound outside the supervisor policy")
	}
	if !validCoreInstance(instance) {
		return errors.New("desktop controller has an invalid core instance")
	}
	record := ControllerRecord{
		ProtocolVersion: ControllerRecordVersion,
		PID:             os.Getpid(),
		Controller:      net.JoinHostPort(ip.String(), strconv.FormatUint(number, 10)),
		CoreInstance:    instance,
		LaunchNonce:     p.LaunchNonce,
	}
	if previous := p.controller.Load(); previous != nil {
		if *previous == record {
			return nil
		}
		return errors.New("desktop controller is already bound to a different instance or endpoint")
	}
	if p.LaunchNonce != "" {
		if err := writeControllerRecord(p.Home, record); err != nil {
			return err
		}
	}
	p.controller.Store(&record)
	return nil
}

// WithdrawController removes only the record published by this process. An
// older process must not remove a newer launch's discovery record during exit.
func WithdrawController() error {
	p := current.Load()
	if p == nil {
		return nil
	}
	p.controllerMu.Lock()
	defer p.controllerMu.Unlock()
	p.ready.Store(false)
	record := p.controller.Swap(nil)
	if record == nil || record.LaunchNonce == "" {
		return nil
	}
	path := filepath.Join(p.Home, ControllerRecordName)
	if err := checkRecordPath(p.Home, path); err != nil {
		return err
	}
	file, err := os.Open(path)
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return fmt.Errorf("read desktop controller record: %w", err)
	}
	data, readErr := io.ReadAll(io.LimitReader(file, ControllerRecordMaxBytes+1))
	closeErr := file.Close()
	if err := errors.Join(readErr, closeErr); err != nil {
		return fmt.Errorf("read desktop controller record: %w", err)
	}
	// Compare the canonical bytes as well as the process identity. This avoids
	// accepting duplicate JSON keys or deleting a record modified by another
	// process, even if it retained a subset of our fields.
	expected, err := marshalControllerRecord(*record)
	if err != nil {
		return err
	}
	if !bytes.Equal(data, expected) {
		return nil
	}
	if err := checkRecordPath(p.Home, path); err != nil {
		return err
	}
	if err := os.Remove(path); err != nil && !os.IsNotExist(err) {
		return fmt.Errorf("remove desktop controller record: %w", err)
	}
	return nil
}

func marshalControllerRecord(record ControllerRecord) ([]byte, error) {
	data, err := json.Marshal(record)
	if err != nil {
		return nil, fmt.Errorf("encode desktop controller record: %w", err)
	}
	data = append(data, '\n')
	if len(data) > ControllerRecordMaxBytes {
		return nil, errors.New("desktop controller record exceeds the size limit")
	}
	return data, nil
}

func checkRecordPath(home, path string) error {
	if err := checkPath(home, path); err != nil {
		return err
	}
	info, err := os.Lstat(path)
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return fmt.Errorf("inspect desktop controller record: %w", err)
	}
	if isLink(info) || !info.Mode().IsRegular() {
		return errors.New("desktop controller record must be a regular file without a link or reparse point")
	}
	return nil
}

func writeControllerRecord(home string, record ControllerRecord) error {
	path := filepath.Join(home, ControllerRecordName)
	if err := checkRecordPath(home, path); err != nil {
		return err
	}
	data, err := marshalControllerRecord(record)
	if err != nil {
		return err
	}
	file, err := os.CreateTemp(home, ".desktop-controller-*.tmp")
	if err != nil {
		return fmt.Errorf("create desktop controller record: %w", err)
	}
	temporary := file.Name()
	defer os.Remove(temporary)
	if _, err := file.Write(data); err != nil {
		file.Close()
		return fmt.Errorf("write desktop controller record: %w", err)
	}
	if err := file.Sync(); err != nil {
		file.Close()
		return fmt.Errorf("sync desktop controller record: %w", err)
	}
	if err := file.Close(); err != nil {
		return fmt.Errorf("close desktop controller record: %w", err)
	}
	if err := checkRecordPath(home, temporary); err != nil {
		return err
	}
	if err := checkRecordPath(home, path); err != nil {
		return err
	}
	if err := os.Rename(temporary, path); err != nil {
		return fmt.Errorf("publish desktop controller record: %w", err)
	}
	return nil
}
