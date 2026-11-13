# Official Clash Verge Rev Service acceptance

This harness installs and runs the **official production** Windows service,
then asks that service to start the newly compiled optimized core. It needs
Python 3.11+, PowerShell 7 and an elevated, disposable GitHub-hosted Windows x64
runner. It refuses other environments, an already registered
`clash_verge_service`, or an existing `ProgramData/clash-verge-service` state
directory. Run it sequentially after the ordinary/managed TUN acceptance.

```powershell
python scripts/windows-cvr-service-compat/run.py `
  --core "$env:RUNNER_TEMP/mihomo-controller-test.exe" `
  --report "$env:RUNNER_TEMP/windows-cvr-service-compat/report.json"
```

Allow a separate **six-minute** workflow step. A passing run should normally
take about one to two minutes; installation, each IPC mutation and uninstall
have finite deadlines. Preserve the report and its sibling evidence directory
with `if: always()`. No Rust build or third-party Python package is required.

## Pins and protocol

All service behavior below is taken from official tag `v2.7.7`, resolved to
commit [`3ed29700b093190b5e7addcaa892e77f172af544`](https://github.com/clash-verge-rev/clash-verge-service-ipc/tree/3ed29700b093190b5e7addcaa892e77f172af544).
The official release workflow builds `standalone client`, without the `test`
feature. Its test driver cannot be reused unchanged because its start command
hardcodes `mock_binary` and a minimal rule-mode configuration.

The downloaded archive is:

```text
https://github.com/clash-verge-rev/clash-verge-service-ipc/releases/download/v2.7.7/clash-verge-service-ipc-v2.7.7-x86_64-pc-windows-msvc.zip
SHA-256 91316df6662b0962d13dcf18f71d0182a62dc5a1a68769a3adc34cda07538bdf
```

The checksum is pinned from the release asset's `digest` in the official
[GitHub release API](https://api.github.com/repos/clash-verge-rev/clash-verge-service-ipc/releases/tags/v2.7.7).
The same release publishes `SHA256SUMS`. Extraction starts only after the full
archive matches this checksum; only its four exact expected files are accepted.
The report also records the official executable digest and tested core digest.

The thin Python client uses the fixed commit's public HTTP-over-named-pipe
protocol; it does not depend on a floating SDK. Requests reuse
`scripts/test-windows-tun-compat.py`, including its bounded subprocess transport
and **server PID check before sending credentials**.

Relevant fixed source contracts:

- [`src/lib.rs`](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/3ed29700b093190b5e7addcaa892e77f172af544/src/lib.rs): production pipe `\\.\pipe\clash-verge-service`, protocol epoch/revision `2.6`, public version handshake.
- [`src/core/structure.rs`](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/3ed29700b093190b5e7addcaa892e77f172af544/src/core/structure.rs) and [`src/client/mod.rs`](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/3ed29700b093190b5e7addcaa892e77f172af544/src/client/mod.rs): owner/session envelopes, `GET /status`, `POST /clash/start`, `DELETE /clash/stop` and `RuntimeBundle`.
- [`src/core/auth.rs`](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/3ed29700b093190b5e7addcaa892e77f172af544/src/core/auth.rs): real Windows SID, 32-byte owner token file, matching owner and protected DACL granting only that SID, LocalSystem and Administrators full access. `windows.ps1` creates these credentials in a fresh temporary directory. Tokens are neither printed nor retained as artifacts.
- [`src/bin/install_service.rs`](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/3ed29700b093190b5e7addcaa892e77f172af544/src/bin/install_service.rs): `--install-service --install-core <absolute path> --sha256 <digest>` stages the test core and starts the SCM LocalSystem service.
- [`src/core/runtime_generation/assets.rs`](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/3ed29700b093190b5e7addcaa892e77f172af544/src/core/runtime_generation/assets.rs): the request's absolute source path is mapped by filename to the installer-approved copy in the protected `cores` directory. The harness verifies the actual process runs that copy.
- [`src/core/manager.rs`](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/3ed29700b093190b5e7addcaa892e77f172af544/src/core/manager.rs) and [`src/core/paths.rs`](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/3ed29700b093190b5e7addcaa892e77f172af544/src/core/paths.rs): the service supplies `-d`, `-f`, `-ext-ctl-pipe` and `LISTEN_NAMEDPIPE_SDDL`. The core pipe is `\\.\pipe\verge-mihomo-production-<SHA256(owner SID)>`; it supplies no `-desktop-managed`.

### GitHub runner file-owner prerequisite

The first actual production-service run reached the official `2.7.7` version
handshake, but `POST /clash/start` returned HTTP 422 / code 1003 before spawning
the core: the newly installed core file's owner was outside SYSTEM,
Administrators and TrustedInstaller. The official installer's `stage_binary`
creates files with `create_new`, without assigning a Windows file owner; its
explicit protected owner/DACL setup applies to the directories. The service's
[`trusted_core_location.rs`](https://github.com/clash-verge-rev/clash-verge-service-ipc/blob/3ed29700b093190b5e7addcaa892e77f172af544/src/core/trusted_core_location.rs)
checks the file owner as well as its access rules and protected parent chain.

The harness now records and, when needed, normalizes **only the freshly installed
test core's owner** before its first start request. It requires the exact unique
approved path and SHA-256, rejects every reparse-point ancestor, and checks the
official service/core directories have trusted owners and protected DACLs. The
file and those directories must grant full access only to SYSTEM and
Administrators. The file may inherit these rules from its protected parent.

An already trusted file owner is left unchanged. Only an owner equal to the
current installer user SID may be changed to Builtin Administrators; other
owners are rejected. The access-only SDDL must remain identical and the file
hash must remain unchanged. The report records the actual before/after SIDs,
access SDDL, hashes and reason. This is a disposable-runner prerequisite, not a
change to the official service or an exemption from its trust checks. Existing
installations, other files and the service executable are never normalized.

## Assertions and cleanup

Two service-managed compatibility cycles run against the same isolated random
TUN name. A separate generation deliberately releases the test's native
resources after both compatibility cycles pass. Each compatibility cycle verifies:

1. The official service commits the correct owner session and reports a core PID.
2. The process's executable, digest, parent service PID and actual argument list
   match the requested new core and ordinary CVR launch contract.
3. The real core pipe's server PID matches that process, `/version` agrees with
   the compiled executable's `-v`, `/configs` has the requested TUN, and
   `/desktop` returns 404.
4. IP Helper reports the upstream generic GUID, original name and valid native
   identity. A raw TCP request to a single test `/32` passes through TUN and
   the in-process loopback SOCKS fixture, which returns an unpredictable body.
5. `DELETE /clash/stop` clears running and desired state, and the held core
   process handle confirms exit. The second start happens without a harness
   wait for adapter disappearance.

The official Windows service implements this normal **service API stop** using
`Child.kill`/`TerminateProcess`; the report does not call it graceful in-core
shutdown. The observed service can leave its stopped TUN's down IP Helper row.
The report preserves the snapshot immediately after the second stop, and neither
compatibility cycle disables TUN before stopping. There is no cleanup between the
first stop and second start.

Only after both cycles finish successfully, the harness runs one separately
recorded `harness_resource_release` generation. The same official service starts
the same approved core and TUN name. The harness checks the file checksum, held
process identity, parent service, arguments, core version and generic TUN identity
again. It then sends one `PATCH /configs {"tun":{"enable":false}}` to that core's
pipe, checking the server PID before the mutation. A subsequent `GET /configs`
must confirm TUN is disabled, the still-running core must release the interface
within a bounded read-only wait, and only then does the harness request the final
official service stop. Its log and report entry are separate from the two
compatibility cycles. A failure in either compatibility cycle prevents this
additional start; it is not a retry or recovery of a failed acceptance cycle.

This deliberate resource-release request is test cleanup. The two earlier
service stop results retain their actual `TerminateProcess` behavior, and the
separate compatibility test also covers clean native resource closure.

In `finally`, the harness rechecks the registered executable/account, official
executable checksum and the exact approved test-core set before using the
official uninstaller. If ownership has changed it fails without uninstalling
the unknown installation. The uninstaller must remove the SCM service and the
approved core, all held test core handles must report exit, and the test TUN
must disappear. The harness never deletes or renames an adapter. It does not
recursively remove `ProgramData`; the official service may leave its state/log
directory on the disposable runner. Existing installations are never test
targets, and this is not a local-machine cleanup utility.

This verifies the official headless service/core boundary at the pinned version.
It does not exercise the CVR GUI, all CVR versions, or arbitrary user
configurations. A successful report is required before claiming this acceptance
has passed.

Portable syntax check (no installation or network access):

```text
python -m py_compile scripts/windows-cvr-service-compat/run.py scripts/test-windows-tun-compat.py
```
