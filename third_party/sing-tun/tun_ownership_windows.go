package tun

import (
	"crypto/md5"
	"errors"
	"fmt"
	"os"
	"strings"
	"syscall"
	"unsafe"

	"github.com/metacubex/sing-tun/internal/winipcfg"
	"github.com/metacubex/sing-tun/internal/wintun"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// The generic sing-tun GUID and "Meta" description are shared by other products.
// These values are supplied to Wintun before it publishes the device, so a crash
// before controller readiness does not leave an unmarked product instance.
const exclusiveWindowsTunnelType = "Mihomo Optimized"

var (
	windowsNetworkClass = windows.GUID{Data1: 0x4d36e972, Data2: 0xe325, Data3: 0x11ce, Data4: [8]byte{0xbf, 0xc1, 0x08, 0x00, 0x2b, 0xe1, 0x03, 0x18}}
	windowsWintunName   = windows.DEVPROPKEY{FmtID: windows.DEVPROPGUID{Data1: 0x3361c968, Data2: 0x2f2e, Data3: 0x4660, Data4: [8]byte{0xb4, 0x7e, 0x69, 0x9c, 0xdc, 0x4c, 0x32, 0xb9}}, PID: 3}

	createExclusiveWindowsAdapter = func(options Options) (windowsAdapter, error) {
		expected := generateExclusiveGUIDByDeviceName(options.Name)
		adapter, err := wintun.CreateAdapter(options.Name, exclusiveWindowsTunnelType, expected)
		if err != nil {
			return nil, err
		}
		// Check the handle returned by Create before assigning addresses or routes.
		// RequestedGUID is a request to Windows, not a substitute for this identity.
		luid := adapter.LUID()
		actual, err := winipcfg.LUID(luid).GUID()
		if err != nil {
			adapter.Close()
			return nil, fmt.Errorf("read created Wintun identity (luid=%d): %w", luid, err)
		}
		if luid == 0 || *actual != *expected {
			adapter.Close()
			return nil, fmt.Errorf("created Wintun identity differs from requested product (luid=%d guid=%s expected_guid=%s)", luid, actual, expected)
		}
		return adapter, nil
	}
	inspectExclusiveWindowsAdapter = inspectWindowsAdapter
)

func generateExclusiveGUIDByDeviceName(name string) *windows.GUID {
	sum := md5.Sum([]byte("mihomo-optimized/wintun-exclusive/v1\x00" + name))
	return (*windows.GUID)(unsafe.Pointer(&sum[0]))
}

type windowsAdapterIdentity struct {
	InstanceID   string
	GUID         windows.GUID
	Name         string
	TunnelType   string
	FriendlyName string
	HardwareIDs  []string
	ClassGUID    windows.GUID
	Phantom      bool
	StatusKnown  bool
	DeviceStatus uint32
	ProblemCode  uint32
}

func (identity windowsAdapterIdentity) matchesProduct(name string, guid windows.GUID) bool {
	// The real PnP instance ID may be decorated by Windows. NetCfgInstanceId and
	// native product properties establish identity; its spelling and present/down
	// state do not establish whether another process holds a software-device handle.
	return identity.InstanceID != "" && identity.GUID == guid && identity.ClassGUID == windowsNetworkClass &&
		strings.EqualFold(identity.Name, name) && identity.TunnelType == exclusiveWindowsTunnelType &&
		identity.FriendlyName == exclusiveWindowsTunnelType+" Tunnel" &&
		len(identity.HardwareIDs) == 1 && strings.EqualFold(identity.HardwareIDs[0], "Wintun")
}

func (identity windowsAdapterIdentity) String() string {
	state := "unknown"
	if identity.StatusKnown {
		state = "present"
		if identity.Phantom {
			state = "phantom"
		}
	}
	return fmt.Sprintf("instance=%q guid=%s class=%s name=%q tunnel_type=%q friendly_name=%q hardware=%q pnp=%s status=0x%x problem=0x%x",
		identity.InstanceID, identity.GUID, identity.ClassGUID, identity.Name, identity.TunnelType,
		identity.FriendlyName, identity.HardwareIDs, state, identity.DeviceStatus, identity.ProblemCode)
}

type windowsInterfaceIdentity struct {
	Alias      string
	GUID       windows.GUID
	LUID       uint64
	Index      uint32
	OperStatus uint32
	AdminState uint32
	Flags      uint8
}

func (identity windowsInterfaceIdentity) String() string {
	return fmt.Sprintf("alias=%q guid=%s luid=%d index=%d oper=%d admin=%d flags=0x%x",
		identity.Alias, identity.GUID, identity.LUID, identity.Index, identity.OperStatus, identity.AdminState, identity.Flags)
}

type windowsAdapterInspection struct {
	ExpectedGUID windows.GUID
	Reason       string
	Interfaces   []windowsInterfaceIdentity
	Native       *windowsAdapterIdentity
}

func (inspection windowsAdapterInspection) failure(stage, reason string, err error) error {
	header := fmt.Sprintf("[wintun stage=%s reason=%s expected_guid=%s", stage, reason, inspection.ExpectedGUID)
	var nativeError syscall.Errno
	if errors.As(err, &nativeError) {
		code := uint32(nativeError)
		header += fmt.Sprintf(" native_code=0x%08x", code)
		if code&0xffff0000 == 0x80070000 {
			header += fmt.Sprintf(" win32_code=%d", code&0xffff)
		}
	}
	var configError windows.CONFIGRET
	if errors.As(err, &configError) {
		header += fmt.Sprintf(" cfgmgr_code=0x%08x", uint32(configError))
	}
	if inspection.Native != nil && inspection.Native.GUID != (windows.GUID{}) {
		header += fmt.Sprintf(" actual_guid=%s", inspection.Native.GUID)
	} else if len(inspection.Interfaces) == 1 {
		header += fmt.Sprintf(" actual_guid=%s", inspection.Interfaces[0].GUID)
	}
	if len(inspection.Interfaces) == 1 {
		row := inspection.Interfaces[0]
		header += fmt.Sprintf(" luid=%d index=%d", row.LUID, row.Index)
	}
	return fmt.Errorf("%s] %w; snapshot={interfaces=%v native={%v}}", header, err, inspection.Interfaces, inspection.Native)
}

func (inspection windowsAdapterInspection) mayDisappear() bool {
	switch inspection.Reason {
	case "foreign_alias", "ambiguous_alias", "native_identity_missing", "identity_mismatch":
		return true
	default:
		return false
	}
}

func inspectWindowsAdapter(name string) (windowsAdapterInspection, error) {
	interfaces, err := winipcfg.GetIfTable2Ex(winipcfg.MibIfEntryNormal)
	if err != nil {
		return windowsAdapterInspection{ExpectedGUID: *generateExclusiveGUIDByDeviceName(name), Reason: "interface_query_failed"}, err
	}
	rows := make([]windowsInterfaceIdentity, 0, len(interfaces))
	for _, entry := range interfaces {
		rows = append(rows, windowsInterfaceIdentity{
			Alias: entry.Alias(), GUID: entry.InterfaceGUID, LUID: uint64(entry.InterfaceLUID),
			Index: entry.InterfaceIndex, OperStatus: uint32(entry.OperStatus),
			AdminState: uint32(entry.AdminStatus), Flags: uint8(entry.InterfaceAndOperStatusFlags),
		})
	}
	return inspectWindowsAdapterInterfaces(name, rows, queryWindowsAdapter)
}

func inspectWindowsAdapterInterfaces(name string, interfaces []windowsInterfaceIdentity, query func(string, windows.GUID) (windowsAdapterIdentity, error)) (windowsAdapterInspection, error) {
	inspection := windowsAdapterInspection{ExpectedGUID: *generateExclusiveGUIDByDeviceName(name)}
	aliases, foreignAlias := 0, false
	for _, entry := range interfaces {
		aliasMatches := strings.EqualFold(entry.Alias, name)
		if aliasMatches || entry.GUID == inspection.ExpectedGUID {
			inspection.Interfaces = append(inspection.Interfaces, entry)
		}
		if aliasMatches {
			aliases++
			foreignAlias = foreignAlias || entry.GUID != inspection.ExpectedGUID
		}
	}
	if foreignAlias {
		inspection.Reason = "foreign_alias"
		return inspection, fmt.Errorf("requested adapter name belongs to a different interface GUID: %w", os.ErrExist)
	}
	if aliases > 1 {
		inspection.Reason = "ambiguous_alias"
		return inspection, errors.New("multiple interfaces match the requested adapter name")
	}
	identity, err := query(name, inspection.ExpectedGUID)
	if err != nil {
		// Only the query's explicit no-device sentinel means absence. A missing
		// property/registry key on a candidate is an inspection failure, not a
		// licence to create over an interface whose identity could not be read.
		if err == os.ErrNotExist {
			if len(inspection.Interfaces) == 0 {
				inspection.Reason = "adapter_absent"
				return inspection, nil
			}
			inspection.Reason = "native_identity_missing"
			return inspection, errors.New("matching interface has no verifiable native Wintun identity")
		}
		inspection.Native = &identity
		inspection.Reason = "native_query_failed"
		return inspection, err
	}
	inspection.Native = &identity
	if !identity.matchesProduct(name, inspection.ExpectedGUID) {
		inspection.Reason = "identity_mismatch"
		return inspection, errors.New("existing native adapter does not match the expected product identity")
	}
	inspection.Reason = "owned_present"
	if identity.Phantom {
		inspection.Reason = "owned_phantom"
	}
	// SwDeviceCreate owns the cross-process lifetime decision. A verified product
	// instance can remain present while its previous removal completes; Windows
	// queues the new creation. An outstanding owner handle instead fails natively.
	return inspection, nil
}

// queryWindowsAdapter is read-only and never loads Wintun. OpenAdapter is not a
// query: it queues Wintun's global orphan cleanup and does not acquire ownership
// of an existing creator's software-device handle.
func queryWindowsAdapter(name string, guid windows.GUID) (windowsAdapterIdentity, error) {
	// Include non-present devices and match their actual NetCfgInstanceId. Neither
	// a hard-coded PnP instance string nor a display name is a native identity.
	devices, err := windows.SetupDiGetClassDevsEx(&windowsNetworkClass, "", 0, 0, 0, "")
	if err != nil {
		return windowsAdapterIdentity{}, err
	}
	defer devices.Close()
	var found *windowsAdapterIdentity
	var unreadableCandidate windowsAdapterIdentity
	var candidateErr error
	for index := 0; ; index++ {
		device, err := devices.EnumDeviceInfo(index)
		if errors.Is(err, windows.ERROR_NO_MORE_ITEMS) {
			if found != nil {
				return *found, nil
			}
			if candidateErr != nil {
				return unreadableCandidate, candidateErr
			}
			return windowsAdapterIdentity{}, os.ErrNotExist
		}
		if err != nil {
			return windowsAdapterIdentity{}, err
		}
		actualGUID, err := readWindowsAdapterGUID(devices, device)
		if err != nil {
			// Uninstalled or unrelated network devices can have no driver key.
			// Preserve a failure for a named Wintun candidate without allowing an
			// unrelated damaged device to prevent all TUN creation on the machine.
			candidateName, nameErr := windows.SetupDiGetDeviceProperty(devices, device, &windowsWintunName)
			if nativeName, ok := candidateName.(string); nameErr == nil && ok && strings.EqualFold(nativeName, name) {
				instanceID, _ := devices.DeviceInstanceID(device)
				unreadableCandidate = windowsAdapterIdentity{InstanceID: instanceID, Name: nativeName, ClassGUID: device.ClassGUID}
				candidateErr = fmt.Errorf("read named Wintun NetCfgInstanceId: %w", err)
			}
			continue
		}
		if actualGUID != guid {
			continue
		}
		identity, err := readWindowsAdapterIdentity(devices, device, actualGUID)
		if err != nil {
			return identity, fmt.Errorf("inspect native adapter %q: %w", name, err)
		}
		if found != nil {
			return identity, fmt.Errorf("multiple native devices have NetCfgInstanceId %s (instances %q and %q)", guid, found.InstanceID, identity.InstanceID)
		}
		found = &identity
	}
}

func readWindowsAdapterGUID(devices windows.DevInfo, device *windows.DevInfoData) (windows.GUID, error) {
	driver, err := devices.OpenDevRegKey(device, windows.DICS_FLAG_GLOBAL, 0, windows.DIREG_DRV, registry.QUERY_VALUE)
	if err != nil {
		return windows.GUID{}, err
	}
	driverKey := registry.Key(driver)
	guidText, _, err := driverKey.GetStringValue("NetCfgInstanceId")
	driverKey.Close()
	if err != nil {
		return windows.GUID{}, err
	}
	return windows.GUIDFromString(guidText)
}

func readWindowsAdapterIdentity(devices windows.DevInfo, device *windows.DevInfoData, guid windows.GUID) (windowsAdapterIdentity, error) {
	identity := windowsAdapterIdentity{GUID: guid, ClassGUID: device.ClassGUID}
	var err error
	identity.InstanceID, err = devices.DeviceInstanceID(device)
	if err != nil {
		return identity, err
	}
	name, err := windows.SetupDiGetDeviceProperty(devices, device, &windowsWintunName)
	if err != nil {
		return identity, err
	}
	var ok bool
	identity.Name, ok = name.(string)
	if !ok {
		return identity, errors.New("invalid Wintun name property")
	}
	description, err := devices.DeviceRegistryProperty(device, windows.SPDRP_DEVICEDESC)
	if err != nil {
		return identity, err
	}
	descriptionText, ok := description.(string)
	if !ok || !strings.HasSuffix(descriptionText, " Tunnel") {
		return identity, errors.New("invalid Wintun device description")
	}
	identity.TunnelType = strings.TrimSuffix(descriptionText, " Tunnel")
	friendly, err := devices.DeviceRegistryProperty(device, windows.SPDRP_FRIENDLYNAME)
	if err != nil {
		return identity, err
	}
	identity.FriendlyName, ok = friendly.(string)
	if !ok {
		return identity, errors.New("invalid Wintun friendly name")
	}
	hardware, err := devices.DeviceRegistryProperty(device, windows.SPDRP_HARDWAREID)
	if err != nil {
		return identity, err
	}
	identity.HardwareIDs, ok = hardware.([]string)
	if !ok {
		return identity, errors.New("invalid Wintun hardware ID")
	}
	err = windows.CM_Get_DevNode_Status(&identity.DeviceStatus, &identity.ProblemCode, device.DevInst, 0)
	switch {
	case err == nil:
		identity.StatusKnown = true
	case errors.Is(err, windows.CR_NO_SUCH_DEVINST):
		identity.StatusKnown = true
		identity.Phantom = true
	default:
		return identity, err
	}
	return identity, nil
}
