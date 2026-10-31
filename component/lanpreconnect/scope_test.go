package lanpreconnect

import (
	"net/netip"
	"sync"
	"testing"
)

func TestLANFakeIPPolicyConcurrentConfigurationReplacement(t *testing.T) {
	old := currentFakeIPPolicy.Load()
	defer currentFakeIPPolicy.Store(old)
	pool4 := netip.MustParsePrefix("192.168.250.0/24")
	pool6 := netip.MustParsePrefix("fd00:1234::/64")
	var workers sync.WaitGroup
	workers.Add(4)
	for i := 0; i < 4; i++ {
		go func() {
			defer workers.Done()
			for i := 0; i < 1000; i++ {
				_ = IsLAN(netip.MustParseAddr("::ffff:192.168.250.7"))
				_ = IsLAN(netip.MustParseAddr("fd00:1234::7"))
				if IsLAN(netip.MustParseAddr("100.64.0.1")) || !IsLAN(netip.MustParseAddr("10.5.6.7")) {
					t.Error("configuration replacement changed the fixed LAN boundary")
					return
				}
			}
		}()
	}
	for i := 0; i < 1000; i++ {
		SetFakeIPPolicy(pool4.Contains)
		SetFakeIPPolicy(pool6.Contains)
		SetFakeIPPolicy(nil)
	}
	workers.Wait()
	SetFakeIPPolicy(pool6.Contains)
	if IsLAN(netip.MustParseAddr("fd00:1234::7")) {
		t.Fatal("latest IPv6 fake range was not excluded")
	}
	SetFakeIPPolicy(nil)
	if !IsLAN(netip.MustParseAddr("fd00:1234::7")) {
		t.Fatal("disabling DNS retained an obsolete fake range")
	}
}
