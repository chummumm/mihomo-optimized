package sing_tun

import (
	"encoding/binary"
	"errors"
	"fmt"
	"net/netip"
	"unsafe"

	tun "github.com/metacubex/sing-tun"
	"golang.org/x/sys/windows"
)

var (
	healthIPHelper    = windows.NewLazySystemDLL("iphlpapi.dll")
	healthIndexToLUID = healthIPHelper.NewProc("ConvertInterfaceIndexToLuid")
	healthGetRoutes   = healthIPHelper.NewProc("GetIpForwardTable2")
	healthFreeTable   = healthIPHelper.NewProc("FreeMibTable")
)

// These are the fixed Windows ABI layouts, including padding on 32-bit Windows.
// https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_ipforward_row2
// https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_ipforward_table2
type healthRouteRow struct {
	LUID                 uint64
	Index                uint32
	Destination          [28]byte // SOCKADDR_INET
	PrefixLength         uint8
	_                    [3]byte
	NextHop              [28]byte // SOCKADDR_INET
	SitePrefixLength     uint8
	_                    [3]byte
	ValidLifetime        uint32
	PreferredLifetime    uint32
	Metric               uint32
	Protocol             uint32
	Loopback             uint8
	AutoconfigureAddress uint8
	Publish              uint8
	Immortal             uint8
	Age                  uint32
	Origin               uint32
}

type healthRouteTable struct {
	Count uint32
	_     [4]byte
	Rows  [1]healthRouteRow
}

func deviceLUID(index int) (uint64, error) {
	if index <= 0 {
		return 0, errors.New("TUN interface index is unavailable")
	}
	var luid uint64
	result, _, _ := healthIndexToLUID.Call(uintptr(index), uintptr(unsafe.Pointer(&luid)))
	if result != 0 {
		return 0, windows.Errno(result)
	}
	if luid == 0 {
		return 0, errors.New("TUN interface LUID is unavailable")
	}
	return luid, nil
}

func (l *Listener) captureDeviceHealth(options tun.Options) error {
	identity, err := deviceLUID(l.tunIndex)
	if err != nil {
		return fmt.Errorf("capture TUN identity: %w", err)
	}
	l.deviceID = identity
	if !options.AutoRoute {
		return nil
	}
	l.routePrefixes, err = options.BuildAutoRouteRanges(false)
	if err != nil {
		return err
	}
	l.routeGateway4, l.routeGateway6 = options.Inet4GatewayAddr(), options.Inet6GatewayAddr()
	return nil
}

// LUID supplements name and ifindex: Windows may recycle an interface index.
func (l *Listener) CheckDeviceIdentity() (bool, error) {
	identity, err := deviceLUID(l.tunIndex)
	return err == nil && identity == l.deviceID && l.deviceID != 0, err
}

func readHealthRoutes() ([]healthRouteRow, error) {
	var table *healthRouteTable
	result, _, _ := healthGetRoutes.Call(windows.AF_UNSPEC, uintptr(unsafe.Pointer(&table)))
	if result != 0 {
		return nil, windows.Errno(result)
	}
	if table == nil {
		return nil, errors.New("Windows returned an empty route table pointer")
	}
	defer healthFreeTable.Call(uintptr(unsafe.Pointer(table)))
	if table.Count > 1<<20 {
		return nil, errors.New("Windows route table exceeds the inspection limit")
	}
	rows := unsafe.Slice(&table.Rows[0], int(table.Count))
	return append([]healthRouteRow(nil), rows...), nil
}

func healthSocketAddress(raw [28]byte) netip.Addr {
	switch binary.LittleEndian.Uint16(raw[:2]) {
	case windows.AF_INET:
		return netip.AddrFrom4([4]byte(raw[4:8]))
	case windows.AF_INET6:
		return netip.AddrFrom16([16]byte(raw[8:24]))
	default:
		return netip.Addr{}
	}
}

func (l *Listener) CheckRoutes() (bool, error) {
	if len(l.routePrefixes) == 0 {
		return true, nil
	}
	rows, err := readHealthRoutes()
	if err != nil {
		return false, err
	}
	return l.hasExpectedRoutes(rows), nil
}

func (l *Listener) hasExpectedRoutes(rows []healthRouteRow) bool {
	type routeKey struct {
		prefix  netip.Prefix
		gateway netip.Addr
	}
	actual := make(map[routeKey]struct{})
	for _, row := range rows {
		if row.Index != uint32(l.tunIndex) || row.LUID != l.deviceID || row.ValidLifetime == 0 {
			continue
		}
		prefix := netip.PrefixFrom(healthSocketAddress(row.Destination), int(row.PrefixLength))
		if prefix.IsValid() {
			actual[routeKey{prefix.Masked(), healthSocketAddress(row.NextHop)}] = struct{}{}
		}
	}
	for _, prefix := range l.routePrefixes {
		gateway := l.routeGateway6
		if prefix.Addr().Is4() {
			gateway = l.routeGateway4
		}
		if _, exists := actual[routeKey{prefix.Masked(), gateway}]; !exists {
			return false
		}
	}
	return true
}
