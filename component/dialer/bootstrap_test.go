package dialer

import "testing"

func TestResourceBootstrapSocketDefaultsAreIsolated(t *testing.T) {
	oldInterface, oldMark := DefaultInterface.Load(), DefaultRoutingMark.Load()
	DefaultInterface.Store("old-runtime")
	DefaultRoutingMark.Store(321)
	t.Cleanup(func() { DefaultInterface.Store(oldInterface); DefaultRoutingMark.Store(oldMark) })
	opt := applyOptions(WithDefaults("candidate", 123), WithInterface("node"), WithRoutingMark(456), WithAddrReuse(true))
	if opt.interfaceDefault() != "candidate" || opt.routingMarkDefault() != 123 || opt.interfaceName != "node" || opt.routingMark != 456 || !opt.addrReuse {
		t.Fatal("node or protocol socket options were discarded")
	}
	zero := applyOptions(WithDefaults("", 0))
	if zero.interfaceDefault() != "" || zero.routingMarkDefault() != 0 {
		t.Fatal("empty candidate defaults leaked the previous runtime's interface or mark")
	}
	normal := applyOptions()
	if normal.interfaceDefault() != "old-runtime" || normal.routingMarkDefault() != 321 {
		t.Fatal("normal runtime defaults changed")
	}
}
