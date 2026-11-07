package tun

import (
	"crypto/md5"
	"errors"
	"fmt"
	"os"
	"strings"
	"unsafe"

	"github.com/metacubex/sing-tun/internal/winipcfg"
	"github.com/metacubex/sing-tun/internal/wintun"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// The generic sing-tun GUID and "Meta" description are shared by other products.
// Both values below are supplied to Wintun at creation, before it publishes the
// device. A crash before the controller becomes ready therefore retains the same
// identity without a separate ownership journal or a post-creation marker gap.
const exclusiveWindowsTunnelType = "Mihomo Optimized"

var (
	windowsNetworkClass = windows.GUID{Data1: 0x4d36e972, Data2: 0xe325, Data3: 0x11ce, Data4: [8]byte{0xbf, 0xc1, 0x08, 0x00, 0x2b, 0xe1, 0x03, 0x18}}
	windowsWintunName   = windows.DEVPROPKEY{FmtID: windows.DEVPROPGUID{Data1: 0x3361c968, Data2: 0x2f2e, Data3: 0x4660, Data4: [8]byte{0xb4, 0x7e, 0x69, 0x9c, 0xdc, 0x4c, 0x32, 0xb9}}, PID: 3}

	createExclusiveWindowsAdapter = func(options Options) (windowsAdapter, error) {
		return wintun.CreateAdapter(options.Name, exclusiveWindowsTunnelType, generateExclusiveGUIDByDeviceName(options.Name))
	}
	canReenumerateWindowsAlias = inspectWindowsAlias
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
}

func exclusiveWindowsInstanceID(guid windows.GUID) string {
	return `SWD\Wintun\` + guid.String()
}

func (identity windowsAdapterIdentity) permitsReenumeration(name string, guid windows.GUID) bool {
	return identity.Phantom && identity.GUID == guid && identity.ClassGUID == windowsNetworkClass &&
		strings.EqualFold(identity.InstanceID, exclusiveWindowsInstanceID(guid)) &&
		strings.EqualFold(identity.Name, name) && identity.TunnelType == exclusiveWindowsTunnelType &&
		identity.FriendlyName == exclusiveWindowsTunnelType+" Tunnel" &&
		len(identity.HardwareIDs) == 1 && strings.EqualFold(identity.HardwareIDs[0], "Wintun")
}

func inspectWindowsAlias(name string) (bool, error) {
	interfaces, err := winipcfg.GetIfTable2Ex(winipcfg.MibIfEntryNormal)
	if err != nil {
		return false, err
	}
	guid := *generateExclusiveGUIDByDeviceName(name)
	matches := 0
	for _, entry := range interfaces {
		if !strings.EqualFold(entry.Alias(), name) {
			continue
		}
		matches++
		if entry.InterfaceGUID != guid {
			return false, nil
		}
	}
	// A vanished alias is checked again by the outer grace loop. Ambiguous
	// aliases cannot authorize re-enumeration, even if one identity matches.
	if matches != 1 {
		return false, nil
	}
	identity, err := queryWindowsAdapter(name, guid)
	if errors.Is(err, os.ErrNotExist) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	return identity.permitsReenumeration(name, guid), nil
}

// queryWindowsAdapter is read-only and never loads Wintun. In particular, do not
// use OpenAdapter as a query: that API queues an unscoped orphan cleanup worker.
func queryWindowsAdapter(name string, guid windows.GUID) (windowsAdapterIdentity, error) {
	devices, err := windows.SetupDiGetClassDevsEx(&windowsNetworkClass, `SWD\Wintun`, 0, 0, 0, "")
	if err != nil {
		return windowsAdapterIdentity{}, err
	}
	defer devices.Close()
	for index := 0; ; index++ {
		device, err := devices.EnumDeviceInfo(index)
		if errors.Is(err, windows.ERROR_NO_MORE_ITEMS) {
			return windowsAdapterIdentity{}, os.ErrNotExist
		}
		if err != nil {
			return windowsAdapterIdentity{}, err
		}
		instanceID, err := devices.DeviceInstanceID(device)
		if err != nil {
			return windowsAdapterIdentity{}, err
		}
		if !strings.EqualFold(instanceID, exclusiveWindowsInstanceID(guid)) {
			continue
		}
		identity, err := readWindowsAdapterIdentity(devices, device)
		if err != nil {
			return windowsAdapterIdentity{}, fmt.Errorf("inspect software device %q: %w", name, err)
		}
		identity.InstanceID = instanceID
		return identity, nil
	}
}

func readWindowsAdapterIdentity(devices windows.DevInfo, device *windows.DevInfoData) (windowsAdapterIdentity, error) {
	identity := windowsAdapterIdentity{ClassGUID: device.ClassGUID}
	driver, err := devices.OpenDevRegKey(device, windows.DICS_FLAG_GLOBAL, 0, windows.DIREG_DRV, registry.QUERY_VALUE)
	if err != nil {
		return identity, err
	}
	driverKey := registry.Key(driver)
	guidText, _, err := driverKey.GetStringValue("NetCfgInstanceId")
	driverKey.Close()
	if err != nil {
		return identity, err
	}
	identity.GUID, err = windows.GUIDFromString(guidText)
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
	var status, problem uint32
	err = windows.CM_Get_DevNode_Status(&status, &problem, device.DevInst, 0)
	switch {
	case err == nil:
		// A down or problem-state device is still present. Neither permits
		// recovery; its owning process may still hold a software-device handle.
	case errors.Is(err, windows.CR_NO_SUCH_DEVINST):
		identity.Phantom = true
	default:
		return identity, err
	}
	return identity, nil
}
