package tun

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"strings"
	"syscall"
	"testing"
	"time"

	"github.com/metacubex/sing-tun/internal/winipcfg"
	"github.com/metacubex/sing-tun/internal/wintun"
	"golang.org/x/sys/windows"
)

// This test binary is a native owner fixture for the ordinary/managed core
// compatibility acceptance test. It is never enabled by the normal test suite.
// Run elevated with -test.run=^TestWindowsCompatOwnerFixture$ -test.timeout=2m
// and MIHOMO_TUN_COMPAT_FIXTURE=1, MIHOMO_TUN_COMPAT_NAME=MihomoCompat-<nonce>,
// MIHOMO_TUN_COMPAT_NAMESPACE=generic|product, MIHOMO_TUN_COMPAT_SESSION=0|1.
// The nonce must contain 12..32 lowercase hexadecimal characters.
//
// Each stdin line is {"command":"inspect|session_start|session_probe|close",
// "id":"caller-token"}. Each stdout JSON event echoes id. Ignore Go test's
// non-JSON status lines. A closed event acknowledges EndSession and Close; its
// identity fields are the last observation immediately before Close, not a
// promise that PnP removal has completed. EOF also closes the owned resources.
// TerminateProcess intentionally bypasses this normal cleanup for crash tests.
func TestWindowsCompatOwnerFixture(t *testing.T) {
	if os.Getenv("MIHOMO_TUN_COMPAT_FIXTURE") != "1" {
		t.Skip("native compatibility owner requires MIHOMO_TUN_COMPAT_FIXTURE=1")
	}
	output := json.NewEncoder(os.Stdout)
	emit := func(event windowsCompatFixtureEvent) {
		t.Helper()
		if err := output.Encode(event); err != nil {
			t.Fatalf("write fixture event: %v", err)
		}
	}
	fatal := func(err error) {
		t.Helper()
		emit(windowsCompatFixtureError("", "startup", nil, err))
		t.Fatal(err)
	}
	if deadline, ok := t.Deadline(); !ok || time.Until(deadline) > 2*time.Minute {
		fatal(errors.New("compatibility fixture requires -test.timeout=2m or shorter"))
	}
	if !windows.GetCurrentProcessToken().IsElevated() {
		fatal(errors.New("compatibility fixture requires an elevated Windows process"))
	}
	name := os.Getenv("MIHOMO_TUN_COMPAT_NAME")
	nonce := strings.TrimPrefix(name, "MihomoCompat-")
	if nonce == name || len(nonce) < 12 || len(nonce) > 32 || strings.Trim(nonce, "0123456789abcdef") != "" {
		fatal(errors.New("compatibility fixture requires an isolated MihomoCompat-<12..32 lowercase hex> name"))
	}
	namespace := os.Getenv("MIHOMO_TUN_COMPAT_NAMESPACE")
	tunnelType, requestedGUID := "Meta", generateGUIDByDeviceName(name)
	switch namespace {
	case "generic":
	case "product":
		tunnelType, requestedGUID = exclusiveWindowsTunnelType, generateExclusiveGUIDByDeviceName(name)
	default:
		fatal(errors.New("MIHOMO_TUN_COMPAT_NAMESPACE must be generic or product"))
	}
	wantSession := os.Getenv("MIHOMO_TUN_COMPAT_SESSION")
	if wantSession != "0" && wantSession != "1" {
		fatal(errors.New("MIHOMO_TUN_COMPAT_SESSION must be 0 or 1"))
	}
	if err := requireUnusedWindowsCompatName(name); err != nil {
		fatal(err)
	}
	adapter, err := wintun.CreateAdapter(name, tunnelType, requestedGUID)
	if err != nil {
		fatal(fmt.Errorf("create fixture adapter: %w", err))
	}
	owner := &windowsCompatFixtureOwner{
		name: name, namespace: namespace, requestedGUID: *requestedGUID,
		adapter: adapter, luid: adapter.LUID(),
	}
	defer owner.close()
	initial, err := owner.snapshot()
	if err != nil {
		fatal(err)
	}
	if initial.GUID != requestedGUID.String() || initial.LUID == 0 || initial.Index == 0 ||
		initial.Alias != name || initial.IfType != uint32(winipcfg.IfTypePropVirtual) ||
		initial.PnPName != name || initial.PnPTunnelType != tunnelType ||
		initial.PnPFriendlyName != tunnelType+" Tunnel" || initial.PnPInstanceID == "" ||
		initial.PnPClassGUID != windowsNetworkClass.String() || initial.PnPPhantom || !initial.PnPStatusKnown ||
		len(initial.PnPHardwareIDs) != 1 || !strings.EqualFold(initial.PnPHardwareIDs[0], "Wintun") {
		fatal(fmt.Errorf("fixture native identity does not match its requested isolated adapter: %+v", initial))
	}
	if wantSession == "1" {
		if err := owner.startSession(); err != nil {
			fatal(err)
		}
	}
	ready, err := owner.snapshot()
	if err != nil {
		fatal(err)
	}
	emit(windowsCompatFixtureEvent{Event: "ready", windowsCompatFixtureSnapshot: ready})

	input := bufio.NewScanner(os.Stdin)
	input.Buffer(make([]byte, 1024), 4096)
	for input.Scan() {
		var command struct {
			Command string `json:"command"`
			ID      string `json:"id"`
		}
		if err := json.Unmarshal(input.Bytes(), &command); err != nil {
			emit(windowsCompatFixtureError("", "decode", nil, err))
			t.Fatalf("decode fixture command: %v", err)
		}
		event := windowsCompatFixtureEvent{ID: command.ID, Event: command.Command}
		var operationErr error
		switch command.Command {
		case "inspect":
		case "session_start":
			operationErr = owner.startSession()
			event.Event = "session_started"
		case "session_probe":
			event.Probe, operationErr = owner.probeSession()
		case "close":
			// Observe before closing. Do not poll for disappearance or load another
			// Wintun handle before the caller's immediate recovery attempt.
			snapshot, err := owner.snapshot()
			owner.close()
			if err != nil {
				emit(windowsCompatFixtureError(command.ID, command.Command, nil, err))
				t.Fatalf("observe fixture before close: %v", err)
			}
			snapshot.SessionActive = false
			emit(windowsCompatFixtureEvent{Event: "closed", ID: command.ID,
				windowsCompatFixtureSnapshot: snapshot, IdentityBeforeClose: true})
			return
		default:
			operationErr = fmt.Errorf("unknown fixture command %q", command.Command)
		}
		snapshot, snapshotErr := owner.snapshot()
		if operationErr == nil {
			operationErr = snapshotErr
		}
		if operationErr != nil {
			// In particular, a failed session_start leaves the creator alive so
			// the caller can inspect it and explicitly close it after contention.
			emit(windowsCompatFixtureError(command.ID, command.Command, snapshot, operationErr))
			continue
		}
		event.windowsCompatFixtureSnapshot = snapshot
		emit(event)
	}
	if err := input.Err(); err != nil {
		emit(windowsCompatFixtureError("", "stdin", nil, err))
		t.Fatalf("read fixture command: %v", err)
	}
}

type windowsCompatFixtureOwner struct {
	name, namespace string
	requestedGUID   windows.GUID
	adapter         *wintun.Adapter
	luid            uint64
	session         wintun.Session
	readEvent       windows.Handle
	sessionActive   bool
}

type windowsCompatFixtureSnapshot struct {
	PID             int      `json:"pid"`
	Name            string   `json:"name"`
	Namespace       string   `json:"namespace"`
	RequestedGUID   string   `json:"requested_guid"`
	GUID            string   `json:"guid"`
	LUID            uint64   `json:"luid"`
	Index           uint32   `json:"index"`
	Alias           string   `json:"alias"`
	Description     string   `json:"description"`
	IfType          uint32   `json:"if_type"`
	OperStatus      uint32   `json:"oper_status"`
	AdminStatus     uint32   `json:"admin_status"`
	PnPInstanceID   string   `json:"pnp_instance_id"`
	PnPName         string   `json:"pnp_name"`
	PnPTunnelType   string   `json:"pnp_tunnel_type"`
	PnPFriendlyName string   `json:"pnp_friendly_name"`
	PnPHardwareIDs  []string `json:"pnp_hardware_ids"`
	PnPClassGUID    string   `json:"pnp_class_guid"`
	PnPPhantom      bool     `json:"pnp_phantom"`
	PnPStatusKnown  bool     `json:"pnp_status_known"`
	PnPStatus       uint32   `json:"pnp_status"`
	PnPProblem      uint32   `json:"pnp_problem"`
	SessionActive   bool     `json:"session_active"`
}

type windowsCompatFixtureEvent struct {
	Event string `json:"event"`
	ID    string `json:"id,omitempty"`
	*windowsCompatFixtureSnapshot
	Probe               *windowsCompatFixtureProbe `json:"probe,omitempty"`
	IdentityBeforeClose bool                       `json:"identity_before_close,omitempty"`
	Command             string                     `json:"command,omitempty"`
	Error               string                     `json:"error,omitempty"`
	NativeCode          *uint32                    `json:"native_code,omitempty"`
}

type windowsCompatFixtureProbe struct {
	ReadEvent   uint64 `json:"read_event"`
	WaitResult  uint32 `json:"wait_result"`
	Receive     string `json:"receive"`
	PacketBytes int    `json:"packet_bytes"`
}

func windowsCompatFixtureError(id, command string, snapshot *windowsCompatFixtureSnapshot, err error) windowsCompatFixtureEvent {
	event := windowsCompatFixtureEvent{Event: "error", ID: id, Command: command,
		windowsCompatFixtureSnapshot: snapshot, Error: err.Error()}
	var native syscall.Errno
	if errors.As(err, &native) {
		code := uint32(native)
		event.NativeCode = &code
	}
	return event
}

func requireUnusedWindowsCompatName(name string) error {
	guids := [2]windows.GUID{*generateGUIDByDeviceName(name), *generateExclusiveGUIDByDeviceName(name)}
	rows, err := winipcfg.GetIfTable2Ex(winipcfg.MibIfEntryNormal)
	if err != nil {
		return err
	}
	for _, row := range rows {
		if strings.EqualFold(row.Alias(), name) || row.InterfaceGUID == guids[0] || row.InterfaceGUID == guids[1] {
			return errors.New("fixture alias or GUID already exists; refusing to create over it")
		}
	}
	for _, guid := range guids {
		if _, err := queryWindowsAdapter(name, guid); err != os.ErrNotExist {
			return fmt.Errorf("fixture native identity is not proven unused (guid=%s): %v", guid, err)
		}
	}
	return nil
}

func (owner *windowsCompatFixtureOwner) snapshot() (*windowsCompatFixtureSnapshot, error) {
	row, err := winipcfg.LUID(owner.luid).Interface()
	if err != nil {
		return nil, fmt.Errorf("read fixture IP Helper identity: %w", err)
	}
	identity, err := queryWindowsAdapter(owner.name, row.InterfaceGUID)
	if err != nil {
		return nil, fmt.Errorf("read fixture PnP identity: %w", err)
	}
	return &windowsCompatFixtureSnapshot{
		PID: os.Getpid(), Name: owner.name, Namespace: owner.namespace,
		RequestedGUID: owner.requestedGUID.String(), GUID: row.InterfaceGUID.String(),
		LUID: uint64(row.InterfaceLUID), Index: row.InterfaceIndex, Alias: row.Alias(), Description: row.Description(),
		IfType: uint32(row.Type), OperStatus: uint32(row.OperStatus), AdminStatus: uint32(row.AdminStatus),
		PnPInstanceID: identity.InstanceID, PnPName: identity.Name, PnPTunnelType: identity.TunnelType,
		PnPFriendlyName: identity.FriendlyName, PnPHardwareIDs: identity.HardwareIDs,
		PnPClassGUID: identity.ClassGUID.String(), PnPPhantom: identity.Phantom,
		PnPStatusKnown: identity.StatusKnown, PnPStatus: identity.DeviceStatus, PnPProblem: identity.ProblemCode,
		SessionActive: owner.sessionActive,
	}, nil
}

func (owner *windowsCompatFixtureOwner) startSession() error {
	if owner.sessionActive {
		return errors.New("fixture already holds a session")
	}
	session, err := owner.adapter.StartSession(wintun.RingCapacityMin)
	if err != nil {
		return fmt.Errorf("start fixture Wintun session: %w", err)
	}
	owner.session, owner.sessionActive = session, true
	owner.readEvent = session.ReadWaitEvent()
	_, err = owner.probeSession()
	return err
}

func (owner *windowsCompatFixtureOwner) probeSession() (*windowsCompatFixtureProbe, error) {
	if !owner.sessionActive {
		return nil, errors.New("fixture has no session to probe")
	}
	event := owner.session.ReadWaitEvent()
	if event == 0 || event != owner.readEvent {
		return nil, errors.New("fixture session read event disappeared or changed")
	}
	state, err := windows.WaitForSingleObject(event, 0)
	if err != nil || (state != windows.WAIT_OBJECT_0 && state != uint32(windows.WAIT_TIMEOUT)) {
		return nil, fmt.Errorf("fixture session read event is unusable (state=%d): %v", state, err)
	}
	probe := &windowsCompatFixtureProbe{ReadEvent: uint64(event), WaitResult: state, Receive: "empty"}
	packet, err := owner.session.ReceivePacket()
	if err == nil {
		if len(packet) == 0 {
			return nil, errors.New("fixture session returned an empty packet")
		}
		probe.Receive, probe.PacketBytes = "packet", len(packet)
		owner.session.ReleaseReceivePacket(packet)
	} else if !errors.Is(err, windows.ERROR_NO_MORE_ITEMS) {
		return nil, fmt.Errorf("fixture session receive failed: %w", err)
	}
	return probe, nil
}

func (owner *windowsCompatFixtureOwner) close() {
	if owner.sessionActive {
		owner.session.End()
		owner.sessionActive = false
	}
	if owner.adapter != nil {
		_ = owner.adapter.Close()
		owner.adapter = nil
	}
}
