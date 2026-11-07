# Pinned Windows ownership patch

This directory contains `github.com/metacubex/sing-tun` **v0.4.28**, copied from
the Go module verified by the repository's `go.sum`. Upstream licenses and
embedded Wintun binaries are retained unchanged. The root `go.mod` replacement
makes the patch reproducible for every build; it does not modify a module cache.

The production changes are limited to `tun_windows.go` and `stack_mixed.go`:

- `NewExclusive` checks the read-only Windows interface table before calling
  Wintun. An existing case-insensitive alias, including a down or hidden
  interface reported by the OS, gets at most 500 ms of read-only polling for
  asynchronous removal after process exit, using one fixed deadline. If still
  present, it returns `os.ErrExist` without creating an adapter; the caller does
  not retry that conflict. Enumeration errors also prevent creation immediately.
  This avoids Wintun 0.14.1's duplicate-name handling, which can rename a
  pre-existing foreign interface.
  The check does not atomically reserve the alias against another process.
  Once a previous process has exited and Windows no longer reports its alias,
  creation may proceed normally. No existing interface is opened or modified by
  the check.
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
permits immediate creation. It also verifies failed creation never opens an
existing adapter, deterministic adapter cleanup and early route failure,
without configuring a real interface.
`stack_cleanup_test.go` verifies that partial startup closes an actual local TCP
listener and unblocks its accept goroutine.
Do not replace this directory with a different upstream release during a network
recovery fix. When refreshing it, reapply and review these changes, rerun
the Windows ownership tests and the desktop's real TUN acceptance test.
