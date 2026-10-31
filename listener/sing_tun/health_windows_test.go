package sing_tun

import (
	"encoding/binary"
	"net/netip"
	"testing"
	"unsafe"

	"golang.org/x/sys/windows"
)

func TestDesktopWindowsRouteABI(t *testing.T) {
	var row healthRouteRow
	var table healthRouteTable
	if unsafe.Sizeof(row) != 104 || unsafe.Offsetof(row.Destination) != 12 || unsafe.Offsetof(row.NextHop) != 44 || unsafe.Offsetof(row.ValidLifetime) != 76 || unsafe.Offsetof(table.Rows) != 8 {
		t.Fatalf("incorrect Windows route ABI: row=%d destination=%d next-hop=%d lifetime=%d table=%d", unsafe.Sizeof(row), unsafe.Offsetof(row.Destination), unsafe.Offsetof(row.NextHop), unsafe.Offsetof(row.ValidLifetime), unsafe.Offsetof(table.Rows))
	}
}

func TestDesktopWindowsRouteInspectionUsesRealKernel(t *testing.T) {
	rows, err := readHealthRoutes()
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) == 0 {
		t.Fatal("Windows route table has no entries, including loopback")
	}
	for _, row := range rows {
		if row.Index > 0 && row.LUID > 0 {
			luid, err := deviceLUID(int(row.Index))
			if err == nil && luid == row.LUID && healthSocketAddress(row.Destination).IsValid() {
				return
			}
		}
	}
	t.Fatal("route table contains no verifiable interface identity")
}

func healthTestSocket(ip string) (raw [28]byte) {
	address := netip.MustParseAddr(ip)
	if address.Is4() {
		binary.LittleEndian.PutUint16(raw[:2], windows.AF_INET)
		bytes := address.As4()
		copy(raw[4:8], bytes[:])
	} else {
		binary.LittleEndian.PutUint16(raw[:2], windows.AF_INET6)
		bytes := address.As16()
		copy(raw[8:24], bytes[:])
	}
	return
}

func TestDesktopWindowsRouteHealthRequiresOwnGateway(t *testing.T) {
	l := &Listener{tunIndex: 42, deviceID: 123,
		routePrefixes: []netip.Prefix{netip.MustParsePrefix("0.0.0.0/0"), netip.MustParsePrefix("::/0")},
		routeGateway4: netip.MustParseAddr("172.19.0.2"), routeGateway6: netip.MustParseAddr("fd00::2")}
	rows := []healthRouteRow{
		{LUID: 123, Index: 42, Destination: healthTestSocket("0.0.0.0"), NextHop: healthTestSocket("172.19.0.2"), ValidLifetime: 0xffffffff},
		{LUID: 123, Index: 42, Destination: healthTestSocket("::"), NextHop: healthTestSocket("fd00::2"), ValidLifetime: 0xffffffff},
	}
	if !l.hasExpectedRoutes(rows) {
		t.Fatal("valid dual-stack owned routes were rejected")
	}
	for _, mutate := range []func(*healthRouteRow){
		func(r *healthRouteRow) { r.LUID++ },
		func(r *healthRouteRow) { r.Index++ },
		func(r *healthRouteRow) { r.NextHop = healthTestSocket("fd00::3") },
		func(r *healthRouteRow) { r.ValidLifetime = 0 },
	} {
		changed := append([]healthRouteRow(nil), rows...)
		mutate(&changed[1])
		if l.hasExpectedRoutes(changed) {
			t.Fatal("foreign, expired or wrong-gateway route was accepted")
		}
	}
}
