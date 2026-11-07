# Pinned Windows ownership patch

This directory contains `github.com/metacubex/sing-tun` **v0.4.28**, copied from
the Go module verified by the repository's `go.sum`. Upstream licenses and
embedded Wintun binaries are retained unchanged. The root `go.mod` replacement
makes the patch reproducible for every build; it does not modify a module cache.

The production changes are limited to `tun_windows.go`,
`tun_ownership_windows.go` and `stack_mixed.go`:

- `NewExclusive` supplies a stable product-specific requested GUID namespace
  (`mihomo-optimized/wintun-exclusive/v1`) and the tunnel type `Mihomo Optimized`
  to Wintun at creation. These values do not change with release versions.
  Wintun supplies the name, device description and friendly name as initial
  software-device properties, so the identity exists before a later controller
  health publication. Generic upstream `New` retains its original namespace.
- Before calling Wintun, a read-only interface-table check rejects a conflicting
  alias, including a down or hidden interface, with a fixed 500 ms grace for
  asynchronous removal. There is one narrow exception: the actual interface
  GUID, alias, exact `SWD\Wintun` instance, network class, Wintun hardware ID and
  product properties must all match, and `CM_Get_DevNode_Status` must return
  exactly `CR_NO_SUCH_DEVINST` for that enumerated instance. Only then may normal
  creation request the same non-present software-device instance. A down,
  disabled or problem-state device is still present and cannot qualify. Native
  query errors prevent creation; foreign and unverified aliases remain conflicts.
  The read-only guard does not atomically reserve aliases against another
  application's simultaneous creation of a different instance.
- The exception repairs forced-process-exit residue. Normal Wintun close
  explicitly removes its device; process termination can leave the non-present
  registration and its alias behind. The old guard prevented all Wintun calls,
  leaving this residue indefinitely. Re-enumeration uses ordinary Create with
  the exact requested instance, never Open, explicit removal or an explicit
  global orphan-cleanup call. Windows allows only one software-device handle per
  instance and arbitrates a competing live owner. This is native exclusivity,
  not proof of prior PID death. The caller still confirms its old process exit.
  Unknown legacy `Meta`/shared-GUID residue remains a conflict: stop its original
  owner normally before upgrading or select an unused `tun.device` name.
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
permits immediate creation. It rejects every mismatched identity and all present
devices, including down/problem state; only an exact verified phantom is admitted.
It also verifies failed creation never opens an existing adapter, deterministic
adapter cleanup and early route failure, without configuring a real interface.
`tun_ownership_integration_windows_test.go`, gated by
`MIHOMO_TUN_INTEGRATION=1` on an elevated Windows runner, uses separate short-lived
test processes so Wintun's asynchronous cleanup from a prior scenario cannot
hide the crash defect. It preserves an independent foreign adapter, verifies
Close-to-immediate-Create, then rejects a live product owner before any native
creation call. After forcibly killing and waiting for that exact owner process,
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
- [Determining whether a device is plugged in](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/determining-whether-a-device-is-plugged-in):
  definitive phantom detection through `CM_Get_DevNode_Status`.

Do not replace this directory with a different upstream release during a network
recovery fix. When refreshing it, reapply and review these changes, rerun
the Windows ownership tests and the desktop's real TUN acceptance test.
