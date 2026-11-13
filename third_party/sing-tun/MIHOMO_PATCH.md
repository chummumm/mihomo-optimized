# Pinned sing-tun patches

This directory contains `github.com/metacubex/sing-tun` **v0.4.29**, plus the
patches below. `UPSTREAM_VERSION` records this baseline and must match the root
`go.mod` requirement. Upstream licenses and
embedded Wintun binaries are retained unchanged. The root `go.mod` replacement
makes the patch reproducible for every build; it does not modify a module cache.

## Windows ownership

The Windows ownership changes are limited to `tun_windows.go`,
`tun_ownership_windows.go` and `stack_mixed.go`:

- `NewExclusive` supplies a stable product-specific requested GUID namespace
  (`mihomo-optimized/wintun-exclusive/v1`) and the tunnel type `Mihomo Optimized`
  to Wintun at creation. These values do not change with release versions.
  Wintun supplies the name, device description and friendly name as initial
  software-device properties, so the identity exists before a later controller
  health publication. Generic upstream `New` retains its original namespace.
- Before calling Wintun, a read-only interface-table check rejects a different
  GUID using the same alias, including a down or hidden interface. Unverified
  conflicting names retain the fixed 500 ms removal grace. A matching PnP
  instance is admitted using its actual NetCfgInstanceId, name, network class,
  Wintun hardware ID and product properties. Its real instance ID is read from
  Windows, and its present/phantom state does not decide creator ownership.
- Normal close can remove the PnP record before IP Helper withdraws its row.
  If the native query returns its exact no-match sentinel, a single row with
  the product GUID, requested alias, nonzero LUID/index and Wintun interface
  type 53 can enter Create immediately. Description is recorded for diagnostics
  but is not a fixed marker: NetCfg makes ifDescr unique and drivers may change
  it. Neither a query error nor a known PnP mismatch can use this path. The
  no-match result is an observation, not proof of creator death or a complete
  historical ownership record.
- These paths use ordinary Create with the exact requested instance, never
  Open, explicit removal or an explicit global orphan-cleanup call. Windows
  allows only one software-device handle per instance and queues same-instance
  creation behind previous removal. The actual returned handle GUID is checked
  before addresses/routes are assigned. The caller still confirms its old
  process exit. Generic legacy `Meta`/shared-GUID interfaces remain protected.
  The read-only guard does not atomically reserve aliases against another
  application's simultaneous creation of a different instance.
- `NewExclusive` also refuses to open a pre-existing adapter after a failed
  create. Mihomo uses this entry point; upstream `New` retains its original
  semantics.
- Failed `StartSession` closes the owned adapter before returning.
- Failed configuration closes the session, adapter and any WFP engine already
  acquired by that attempt.
- `InterfaceLUID` exposes the identity of the actual owned adapter handle for
  verification against name/index lookups.
- A failed automatic route returns immediately with the affected prefix, so a
  later successful route cannot hide a partially configured adapter.
- `Mixed.Close` releases partially started system TCP listeners even when IPv6
  or gVisor initialization failed before the IP stack was assigned. The Mihomo
  listener registers ownership of the stack before calling `Start`.

`tun_ownership_windows_test.go` verifies with a fake clock that persistent alias
conflicts and lookup failures never reach the adapter factory, alias removal
within the fixed grace period permits exactly one create, and an absent alias
permits immediate creation. Verified product PnP instances and the qualified
IP Helper-only residual row reach exactly one native creation without waiting.
Every mismatched native identity, incomplete residual row and query error remains
rejected; display descriptions cannot change the decision.
It also verifies failed creation never opens an existing adapter, deterministic
adapter cleanup and early route failure, without configuring a real interface.
`tun_ownership_integration_windows_test.go`, gated by
`MIHOMO_TUN_INTEGRATION=1` on an elevated Windows runner, uses separate short-lived
test processes so Wintun's asynchronous cleanup from a prior scenario cannot
hide the crash defect. It preserves an independent foreign adapter, verifies
Close-to-immediate-Create, and confirms native refusal of a live product owner.
A deterministic residual-window test captures a real interface row, supplies
that old read-only snapshot with a no-match PnP observation, and verifies both
live-owner protection and single-call recreation after close. The Wintun calls
and sessions are real; only the two enumeration views are controlled. After
forcibly killing and waiting for that exact owner process,
one production constructor must regain the same product instance/GUID/alias
with a usable native session. The test counts actual factory invocations and
does not call native Open or dummy Create before the recovery under test.
`stack_cleanup_test.go` verifies that partial startup closes an actual local TCP
listener and unblocks its accept goroutine.

Native behavior is based on Wintun **0.14.1** `api/adapter.c` (the unchanged
embedded DLL version) and Microsoft's documentation:

- [Wintun 0.14.1 adapter implementation](https://github.com/WireGuard/wintun/blob/0.14.1/api/adapter.c): requested GUID as the software-device instance ID,
  creation-time properties, explicit device removal during normal close and
  asynchronous orphan cleanup queued by Create/Open/Close.
- [SwDeviceClose](https://learn.microsoft.com/en-us/windows/win32/api/swdevice/nf-swdevice-swdeviceclose): unplugging is asynchronous and does not uninstall
  the device registration; immediate same-instance creation is serialized.
- [SwDeviceSetLifetime](https://learn.microsoft.com/en-us/windows/win32/api/swdevice/nf-swdevice-swdevicesetlifetime): handle lifetime and at most one software-device
  handle per instance.
- [Network interfaces](https://learn.microsoft.com/en-us/windows/win32/network-interfaces): GUID-based interface identity and mutable/decorated descriptions.
- [Determining whether a device is plugged in](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/determining-whether-a-device-is-plugged-in):
  definitive phantom detection through `CM_Get_DevNode_Status`.

Do not replace this directory with a different upstream release during a network
recovery fix. When refreshing it, reapply and review these changes, rerun
the Windows ownership tests and the desktop's real TUN acceptance test.

## Deferred TCP handshakes

`stack_tcp_lazy.go`, `stack_mipstack_tcp.go`, `stack_gvisor_tcp.go` and
`stack_gvisor.go` let the TCP handler decide an intercepted SYN before the
local handshake is accepted. They use the existing sing `HandshakeSuccess`,
`HandshakeFailure` and `EarlyConn` interfaces; `HandshakeDrop` is the explicit
pending-request drop action. Reading or writing for a real sniffer can accept
the handshake early. Setting deadlines alone does not accept it, and the
deadline of a read or success report also bounds waiting for the client's ACK.

The original mipstack forwarder callback retains the request and performs its
one Accept/Reject/Drop action. Request completion only cancels pending work;
it is not carried into an accepted connection's lifetime. Closing a stack also
releases undecided requests. Accepted connections retain CloseRead/CloseWrite,
so a client's FIN does not discard the reverse response.

`stack_tcp_lazy_test.go` uses in-memory IPv4/IPv6 packets and the real mips and
gvisor stacks. It checks deferred SYN ACK, rejection without prior SYN ACK,
silent pending drop, sniff-triggered acceptance, reverse data after FIN,
pending stack cleanup, and missing-ACK deadlines. Run these tests with
`go test -tags with_gvisor -run '^TestTCP' .` from this directory. The internal
platform packages are not all buildable on a single host, so do not use `./...`
as this test gate.

The pinned mipstack `9a80a07eaeb9` has a verified limitation after a sniffer has
already accepted a flow: its Close can queue FIN before publishing the abort,
even when SetLinger(0) precedes Close. The test therefore requires the eventual
RST for that accepted mips flow; gvisor must reset directly. Pending rejection
does not use this close path and sends RST without SYN ACK, while pending drop
sends neither. This patch does not change mipstack or claim to reconstruct
remote errno values or preserve every remote packet sequence.
