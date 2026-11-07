package sing_tun

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

var (
	healthGetAddresses = healthIPHelper.NewProc("GetUnicastIpAddressTable")
	healthInitAddress  = healthIPHelper.NewProc("InitializeUnicastIpAddressEntry")
	healthAddAddress   = healthIPHelper.NewProc("CreateUnicastIpAddressEntry")
	healthInitRoute    = healthIPHelper.NewProc("InitializeIpForwardEntry")
	healthAddRoute     = healthIPHelper.NewProc("CreateIpForwardEntry2")
)

// Fixed Windows ABI, including the alignment needed by 32-bit callers.
// https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_unicastipaddress_row
type healthAddressRow struct {
	Address           [28]byte
	_                 [4]byte
	LUID              uint64
	Index             uint32
	PrefixOrigin      uint32
	SuffixOrigin      uint32
	ValidLifetime     uint32
	PreferredLifetime uint32
	PrefixLength      uint8
	SkipAsSource      uint8
	_                 [2]byte
	DadState          uint32
	ScopeID           uint32
	CreatedAt         int64
}

type healthAddressTable struct {
	Count uint32
	_     [4]byte
	Rows  [1]healthAddressRow
}

const (
	healthDadTentative = 1
	healthDadPreferred = 4
)

func readHealthAddresses() ([]healthAddressRow, error) {
	var table *healthAddressTable
	result, _, _ := healthGetAddresses.Call(windows.AF_UNSPEC, uintptr(unsafe.Pointer(&table)))
	if result != 0 {
		return nil, windows.Errno(result)
	}
	if table == nil {
		return nil, errors.New("Windows returned an empty address table pointer")
	}
	defer healthFreeTable.Call(uintptr(unsafe.Pointer(table)))
	if table.Count > 1<<20 {
		return nil, errors.New("Windows address table exceeds the inspection limit")
	}
	return append([]healthAddressRow(nil), unsafe.Slice(&table.Rows[0], int(table.Count))...), nil
}

func makeHealthSocket(address netip.Addr) (raw [28]byte) {
	if address.Is4() {
		binary.LittleEndian.PutUint16(raw[:2], windows.AF_INET)
		bytes := address.As4()
		copy(raw[4:8], bytes[:])
	} else if address.Is6() {
		binary.LittleEndian.PutUint16(raw[:2], windows.AF_INET6)
		bytes := address.As16()
		copy(raw[8:24], bytes[:])
	}
	return
}

func addHealthAddress(luid uint64, prefix netip.Prefix) error {
	var row healthAddressRow
	healthInitAddress.Call(uintptr(unsafe.Pointer(&row)))
	row.LUID = luid // LUID is authoritative; never fall back to a recycled index.
	row.Address = makeHealthSocket(prefix.Addr())
	row.PrefixLength = uint8(prefix.Bits())
	row.ValidLifetime, row.PreferredLifetime = ^uint32(0), ^uint32(0)
	// Do not mark a tentative address preferred ourselves. Windows owns DAD.
	result, _, _ := healthAddAddress.Call(uintptr(unsafe.Pointer(&row)))
	if result != 0 {
		return windows.Errno(result)
	}
	return nil
}

func addHealthRoute(luid uint64, prefix netip.Prefix, gateway netip.Addr) error {
	var row healthRouteRow
	healthInitRoute.Call(uintptr(unsafe.Pointer(&row)))
	row.LUID = luid
	row.Destination = makeHealthSocket(prefix.Masked().Addr())
	row.PrefixLength = uint8(prefix.Bits())
	row.NextHop = makeHealthSocket(gateway)
	row.Metric = 0 // Matches sing-tun's original automatic route.
	result, _, _ := healthAddRoute.Call(uintptr(unsafe.Pointer(&row)))
	if result != 0 {
		return windows.Errno(result)
	}
	return nil
}

func verifyHealthIdentity(name string, index int, expected uint64) error {
	device, err := net.InterfaceByName(name)
	if err != nil {
		return fmt.Errorf("inspect owned TUN: %w", err)
	}
	if expected == 0 || device.Name != name || device.Index != index {
		return errors.New("TUN device identity changed before repair")
	}
	actual, err := deviceLUID(index)
	if err != nil {
		return err
	}
	if actual != expected {
		return errors.New("TUN device LUID changed before repair")
	}
	if device.Flags&net.FlagUp == 0 {
		return errors.New("owned TUN device is down")
	}
	return nil
}

func (l *Listener) ownAddress(rows []healthAddressRow, address netip.Addr) *healthAddressRow {
	for i := range rows {
		row := &rows[i]
		if row.LUID == l.deviceID && row.Index == uint32(l.tunIndex) && healthSocketAddress(row.Address) == address {
			return row
		}
	}
	return nil
}

func usableHealthAddress(row *healthAddressRow, prefix netip.Prefix) bool {
	return row != nil && int(row.PrefixLength) == prefix.Bits() && row.ValidLifetime != 0 &&
		row.PreferredLifetime != 0 && row.DadState == healthDadPreferred
}

func (l *Listener) hasExpectedAddresses(rows []healthAddressRow) bool {
	for _, prefix := range l.addressPrefixes {
		if !usableHealthAddress(l.ownAddress(rows, prefix.Addr()), prefix) {
			return false
		}
	}
	return true
}

func (l *Listener) CheckAddresses() (bool, error) {
	rows, err := readHealthAddresses()
	if err != nil {
		return false, err
	}
	return l.hasExpectedAddresses(rows), nil
}

type windowsRepairOps struct {
	identity   func(string, int, uint64) error
	addresses  func() ([]healthAddressRow, error)
	routes     func() ([]healthRouteRow, error)
	addAddress func(uint64, netip.Prefix) error
	addRoute   func(uint64, netip.Prefix, netip.Addr) error
	wait       func(context.Context) error
}

func waitAddressReadiness(ctx context.Context) error {
	timer := time.NewTimer(100 * time.Millisecond)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}

// RepairResources only adds missing entries belonging to the verified adapter.
// It never flushes an address family, removes a foreign route or recreates TUN.
// The caller serializes it with close/configuration; every write revalidates the
// actual interface identity and context. Native IP Helper calls remain serial.
func (l *Listener) RepairResources(ctx context.Context) error {
	ctx, cancel := context.WithTimeout(ctx, 8*time.Second)
	defer cancel()
	return l.repairWindowsResources(ctx, windowsRepairOps{
		identity: verifyHealthIdentity, addresses: readHealthAddresses, routes: readHealthRoutes,
		addAddress: addHealthAddress, addRoute: addHealthRoute, wait: waitAddressReadiness,
	})
}

func (l *Listener) repairWindowsResources(ctx context.Context, ops windowsRepairOps) error {
	verify := func() error {
		if err := ctx.Err(); err != nil {
			return err
		}
		return ops.identity(l.tunName, l.tunIndex, l.deviceID)
	}
	if err := verify(); err != nil {
		return err
	}
	rows, err := ops.addresses()
	if err != nil {
		return fmt.Errorf("inspect TUN addresses before repair: %w", err)
	}
	for _, prefix := range l.addressPrefixes {
		if row := l.ownAddress(rows, prefix.Addr()); row != nil {
			if int(row.PrefixLength) != prefix.Bits() {
				return fmt.Errorf("owned TUN address %s has a different prefix length", prefix.Addr())
			}
			continue
		}
		if err := verify(); err != nil {
			return err
		}
		if err := ops.addAddress(l.deviceID, prefix); err != nil && !healthEntryExists(err) {
			return fmt.Errorf("restore TUN address %s: %w", prefix, err)
		}
	}
	// Existing and newly added entries must actually be usable before routing
	// traffic to them. EEXIST is not proof of either ownership or readiness.
	for {
		if err := verify(); err != nil {
			return err
		}
		rows, err = ops.addresses()
		if err != nil {
			return fmt.Errorf("verify restored TUN addresses: %w", err)
		}
		if l.hasExpectedAddresses(rows) {
			break
		}
		for _, prefix := range l.addressPrefixes {
			row := l.ownAddress(rows, prefix.Addr())
			if row == nil || int(row.PrefixLength) != prefix.Bits() || row.ValidLifetime == 0 || row.PreferredLifetime == 0 ||
				(row.DadState != healthDadTentative && row.DadState != healthDadPreferred) {
				return fmt.Errorf("restored TUN address %s is missing, expired or rejected by DAD", prefix)
			}
		}
		if err := ops.wait(ctx); err != nil {
			return fmt.Errorf("wait for TUN address DAD: %w", err)
		}
	}
	if len(l.routePrefixes) == 0 {
		return verify()
	}
	routes, err := ops.routes()
	if err != nil {
		return fmt.Errorf("inspect TUN routes before repair: %w", err)
	}
	for _, prefix := range l.routePrefixes {
		gateway := l.routeGateway6
		if prefix.Addr().Is4() {
			gateway = l.routeGateway4
		}
		if l.hasHealthRoute(routes, prefix, gateway) {
			continue
		}
		if err := verify(); err != nil {
			return err
		}
		if err := ops.addRoute(l.deviceID, prefix, gateway); err != nil && !healthEntryExists(err) {
			return fmt.Errorf("restore TUN route %s: %w", prefix, err)
		}
	}
	if err := verify(); err != nil {
		return err
	}
	routes, err = ops.routes()
	if err != nil {
		return fmt.Errorf("verify restored TUN routes: %w", err)
	}
	if !l.hasExpectedRoutes(routes) {
		return errors.New("TUN routes remain missing after repair")
	}
	return nil
}

func healthEntryExists(err error) bool {
	return errors.Is(err, windows.ERROR_OBJECT_ALREADY_EXISTS) || errors.Is(err, windows.ERROR_ALREADY_EXISTS)
}

func (l *Listener) hasHealthRoute(rows []healthRouteRow, prefix netip.Prefix, gateway netip.Addr) bool {
	for _, row := range rows {
		if row.LUID == l.deviceID && row.Index == uint32(l.tunIndex) && row.ValidLifetime != 0 &&
			netip.PrefixFrom(healthSocketAddress(row.Destination), int(row.PrefixLength)).Masked() == prefix.Masked() &&
			healthSocketAddress(row.NextHop) == gateway {
			return true
		}
	}
	return false
}
