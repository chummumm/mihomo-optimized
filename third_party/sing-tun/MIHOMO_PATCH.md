# Pinned Windows ownership patch

This directory contains `github.com/metacubex/sing-tun` **v0.4.28**, copied from
the Go module verified by the repository's `go.sum`. Upstream licenses and
embedded Wintun binaries are retained unchanged. The root `go.mod` replacement
makes the patch reproducible for every build; it does not modify a module cache.

The production changes are limited to `tun_windows.go` and `stack_mixed.go`:

- `NewExclusive` refuses to open a pre-existing adapter after a failed create.
  Mihomo uses this entry point; upstream `New` retains its original semantics.
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

`tun_ownership_windows_test.go` verifies exclusive creation, deterministic
adapter cleanup and early route failure, without configuring a real interface.
`stack_cleanup_test.go` verifies that partial startup closes an actual local TCP
listener and unblocks its accept goroutine.
Do not replace this directory with a different upstream release during a network
recovery fix. When refreshing it, reapply and review these changes, rerun
the Windows ownership tests and the desktop's real TUN acceptance test.
