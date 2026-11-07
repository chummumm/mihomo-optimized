package sing_tun

import (
	"context"
	"errors"
	"net/netip"
	"testing"
	"time"
	"unsafe"

	tun "github.com/metacubex/sing-tun"
	"golang.org/x/sys/windows"
)

func TestDesktopWindowsAddressABIAndNativeRead(t *testing.T) {
	var row healthAddressRow
	var table healthAddressTable
	if unsafe.Sizeof(row) != 80 || unsafe.Offsetof(row.LUID) != 32 || unsafe.Offsetof(row.Index) != 40 ||
		unsafe.Offsetof(row.DadState) != 64 || unsafe.Offsetof(row.CreatedAt) != 72 || unsafe.Offsetof(table.Rows) != 8 {
		t.Fatalf("incorrect Windows address ABI: row=%d LUID=%d index=%d DAD=%d created=%d table=%d", unsafe.Sizeof(row),
			unsafe.Offsetof(row.LUID), unsafe.Offsetof(row.Index), unsafe.Offsetof(row.DadState), unsafe.Offsetof(row.CreatedAt), unsafe.Offsetof(table.Rows))
	}
	rows, err := readHealthAddresses()
	if err != nil {
		t.Fatal(err)
	}
	for _, row := range rows {
		if row.LUID != 0 && row.Index != 0 && healthSocketAddress(row.Address).IsValid() {
			actual, err := deviceLUID(int(row.Index))
			if err == nil && actual == row.LUID {
				return
			}
		}
	}
	t.Fatal("Windows returned no address with a verifiable interface identity")
}

type repairFixture struct {
	l                                             *Listener
	addresses                                     []healthAddressRow
	routes                                        []healthRouteRow
	addressAdds, routeAdds, identityChecks, waits int
	identityError                                 error
	addAddressError, addRouteError                error
	addAddressNoEffect, addRouteNoEffect          bool
	tentative                                     bool
}

func newRepairFixture() *repairFixture {
	return &repairFixture{l: &Listener{tunName: "owned-tun", tunIndex: 42, deviceID: 123,
		addressPrefixes: []netip.Prefix{netip.MustParsePrefix("172.19.0.1/30"), netip.MustParsePrefix("fd00::1/126")},
		routePrefixes:   []netip.Prefix{netip.MustParsePrefix("0.0.0.0/0"), netip.MustParsePrefix("::/0")},
		routeGateway4:   netip.MustParseAddr("172.19.0.2"), routeGateway6: netip.MustParseAddr("fd00::2")}}
}

func (f *repairFixture) ops(t *testing.T) windowsRepairOps {
	t.Helper()
	return windowsRepairOps{
		identity: func(name string, index int, luid uint64) error {
			f.identityChecks++
			if name != f.l.tunName || index != f.l.tunIndex || luid != f.l.deviceID {
				t.Fatal("wrong ownership barrier")
			}
			return f.identityError
		},
		addresses: func() ([]healthAddressRow, error) { return append([]healthAddressRow(nil), f.addresses...), nil },
		routes:    func() ([]healthRouteRow, error) { return append([]healthRouteRow(nil), f.routes...), nil },
		addAddress: func(luid uint64, prefix netip.Prefix) error {
			f.addressAdds++
			if luid != f.l.deviceID {
				t.Fatal("write targeted a foreign adapter")
			}
			if !f.addAddressNoEffect {
				dad := uint32(healthDadPreferred)
				if f.tentative {
					dad = healthDadTentative
				}
				f.addresses = append(f.addresses, healthAddressRow{LUID: luid, Index: uint32(f.l.tunIndex),
					Address: makeHealthSocket(prefix.Addr()), PrefixLength: uint8(prefix.Bits()),
					ValidLifetime: ^uint32(0), PreferredLifetime: ^uint32(0), DadState: dad})
			}
			return f.addAddressError
		},
		addRoute: func(luid uint64, prefix netip.Prefix, gateway netip.Addr) error {
			f.routeAdds++
			if luid != f.l.deviceID || !f.l.hasExpectedAddresses(f.addresses) {
				t.Fatal("route written before owned addresses were ready")
			}
			if !f.addRouteNoEffect {
				f.routes = append(f.routes, healthRouteRow{LUID: luid, Index: uint32(f.l.tunIndex), Destination: makeHealthSocket(prefix.Masked().Addr()),
					PrefixLength: uint8(prefix.Bits()), NextHop: makeHealthSocket(gateway), ValidLifetime: ^uint32(0)})
			}
			return f.addRouteError
		},
		wait: func(ctx context.Context) error {
			f.waits++
			if err := ctx.Err(); err != nil {
				return err
			}
			for i := range f.addresses {
				f.addresses[i].DadState = healthDadPreferred
			}
			return nil
		},
	}
}

func TestDesktopWindowsRepairAddsOnlyMissingOwnedResources(t *testing.T) {
	f := newRepairFixture()
	ops := f.ops(t)
	if err := ops.addAddress(f.l.deviceID, f.l.addressPrefixes[0]); err != nil {
		t.Fatal(err)
	}
	f.addressAdds = 0
	// Unrelated entries, including a foreign same-address entry, must survive.
	f.addresses = append(f.addresses, healthAddressRow{LUID: 999, Index: 42, Address: makeHealthSocket(f.l.addressPrefixes[1].Addr())})
	f.routes = append(f.routes, healthRouteRow{LUID: 999, Index: 42, ValidLifetime: 1})
	if err := f.l.repairWindowsResources(context.Background(), ops); err != nil {
		t.Fatal(err)
	}
	if f.addressAdds != 1 || f.routeAdds != 2 || f.l.deviceID != 123 || f.l.tunIndex != 42 || f.addresses[1].LUID != 999 || f.routes[0].LUID != 999 {
		t.Fatalf("repair changed ownership or existing entries: %+v", f)
	}
	if err := f.l.repairWindowsResources(context.Background(), ops); err != nil {
		t.Fatal(err)
	}
	if f.addressAdds != 1 || f.routeAdds != 2 {
		t.Fatal("healthy resources were rewritten")
	}
}

func TestDesktopWindowsRepairWaitsForDADAndVerifiesAlreadyExists(t *testing.T) {
	f := newRepairFixture()
	f.tentative = true
	f.addAddressError, f.addRouteError = windows.ERROR_OBJECT_ALREADY_EXISTS, windows.ERROR_OBJECT_ALREADY_EXISTS
	if err := f.l.repairWindowsResources(context.Background(), f.ops(t)); err != nil {
		t.Fatal(err)
	}
	if f.waits != 1 || f.routeAdds != 2 {
		t.Fatalf("DAD was not respected: %+v", f)
	}
	for _, resource := range []string{"address", "route"} {
		t.Run(resource, func(t *testing.T) {
			f := newRepairFixture()
			if resource == "address" {
				f.addAddressNoEffect = true
				f.addAddressError = windows.ERROR_OBJECT_ALREADY_EXISTS
			} else {
				f.addRouteNoEffect = true
				f.addRouteError = windows.ERROR_OBJECT_ALREADY_EXISTS
			}
			if err := f.l.repairWindowsResources(context.Background(), f.ops(t)); err == nil {
				t.Fatal("EEXIST without verified resources was accepted")
			}
		})
	}
}

func TestDesktopWindowsAddressHealthRejectsUnusableOrForeign(t *testing.T) {
	f := newRepairFixture()
	if err := f.l.repairWindowsResources(context.Background(), f.ops(t)); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*healthAddressRow){
		func(r *healthAddressRow) { r.LUID++ }, func(r *healthAddressRow) { r.Index++ },
		func(r *healthAddressRow) { r.PrefixLength++ }, func(r *healthAddressRow) { r.ValidLifetime = 0 },
		func(r *healthAddressRow) { r.PreferredLifetime = 0 }, func(r *healthAddressRow) { r.DadState = healthDadTentative },
		func(r *healthAddressRow) { r.DadState = 2 },
	} {
		rows := append([]healthAddressRow(nil), f.addresses...)
		mutate(&rows[0])
		if f.l.hasExpectedAddresses(rows) {
			t.Fatal("foreign, expired or unusable address reported healthy")
		}
	}
}

func TestDesktopWindowsRepairStopsAtIdentityChangeOrCancellation(t *testing.T) {
	for _, failure := range []string{"foreign", "cancelled", "enumeration", "changed-before-write"} {
		t.Run(failure, func(t *testing.T) {
			f := newRepairFixture()
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			ops := f.ops(t)
			switch failure {
			case "foreign":
				f.identityError = errors.New("foreign LUID")
			case "cancelled":
				cancel()
			case "enumeration":
				ops.addresses = func() ([]healthAddressRow, error) { return nil, errors.New("IP Helper unavailable") }
			case "changed-before-write":
				ops.addresses = func() ([]healthAddressRow, error) { f.identityError = errors.New("identity changed"); return nil, nil }
			}
			if err := f.l.repairWindowsResources(ctx, ops); err == nil {
				t.Fatal("unverified mutation permitted")
			}
			if f.addressAdds != 0 || f.routeAdds != 0 {
				t.Fatal("wrote after cancellation/ownership failure")
			}
		})
	}
}

func TestDesktopWindowsTunRetryUsesErrorClassAndSingleBudget(t *testing.T) {
	for _, tc := range []struct {
		name     string
		failure  error
		cost     time.Duration
		attempts int
	}{
		{"fast-transient", windows.ERROR_BUSY, 0, 3}, {"slow-transient", windows.ERROR_RETRY, 4 * time.Second, 1},
		{"name-conflict", windows.ERROR_ALREADY_EXISTS, 0, 1}, {"permission", windows.ERROR_ACCESS_DENIED, 0, 1},
		{"unknown", errors.New("unknown failure"), 0, 1},
	} {
		t.Run(tc.name, func(t *testing.T) {
			now := time.Unix(1, 0)
			attempts := 0
			_, err := createWindowsTun(tun.Options{}, func(tun.Options) (tun.Tun, error) { attempts++; now = now.Add(tc.cost); return nil, tc.failure },
				func() time.Time { return now }, func(delay time.Duration) { now = now.Add(delay) })
			if !errors.Is(err, tc.failure) || attempts != tc.attempts {
				t.Fatalf("err=%v attempts=%d want=%d", err, attempts, tc.attempts)
			}
		})
	}
}
