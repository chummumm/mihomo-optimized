# Desktop supervisor contract (API 1)

The standalone daemon keeps its existing behavior. A desktop supervisor opts in
with `-desktop-managed` or `CLASH_DESKTOP_MANAGED=true` before configuration is
parsed. This is an immutable process policy; it is not a YAML setting.

## Bootstrap

Create a private, existing data directory first. Start the core with:

```text
mihomo.exe -desktop-managed -d <absolute-home> -f <absolute-home/config.yaml> -ext-ctl 127.0.0.1:<reserved-port>
```

Generate at least 32 random bytes using the operating system CSPRNG and encode
them as base64url. Pass the resulting secret only through `CLASH_OVERRIDE_SECRET`
in the child environment, not the command line or YAML. The core rejects missing,
short, non-ASCII or whitespace-containing secrets and non-loopback controller
addresses. IP literals and explicit ports 1..65535 are required. The supervisor
must reserve a distinct application instance and wait for the authenticated
`GET /desktop` response from its child PID before considering it ready.

The home must exist and cannot itself be a symbolic link or reparse point.
The configuration file and managed provider/database/cache/state paths are
confined to that home. Managed mode ignores `SAFE_PATHS` and
`SKIP_SAFE_PATH_CHECK`. Runtime filesystem checks reject symbolic links,
reparse points, special files and Windows alternate data stream names, including
substitution of the canonical home's parent directories after bootstrap.
The supervisor must also protect the home and its parent directory ACLs;
path checks do not replace operating-system ownership or prevent an authorized
writer from racing directory changes. A Windows service must choose its own
fixed executable and home, never accept arbitrary paths/arguments/environment
from an IPC caller, and authenticate and restrict its own IPC independently.

The core requires file-based configuration in this mode and rejects
`-post-up`/`-post-down` scripts. Service provisioning, installation, credentials,
system proxy restoration and binary authenticity remain supervisor duties.

## Control channel

All desktop endpoints are behind the regular Bearer authentication middleware.
The core forces the selected loopback address and secret, clears TLS/Unix/pipe
and unauthenticated DoH controller endpoints, and disables core-hosted UI and
debug-profiler routes. This applies after startup overrides and across reloads.
Allowed browser origins are `tauri://localhost`, `http://tauri.localhost`, and
`https://tauri.localhost`, plus the fixed development origin
`http://localhost:3000`; native HTTP clients are unaffected by browser CORS.

`GET /desktop` returns JSON without the secret:

```json
{
  "managed": true,
  "api_version": 1,
  "state": "running",
  "pid": 1234,
  "platform": "windows",
  "arch": "amd64",
  "core_version": "<build-version>",
  "controller": "127.0.0.1:<port>",
  "config_path": "<absolute-home/config.yaml>",
  "update_owner": "desktop",
  "capabilities": {
    "config_edit": true,
    "config_reload": true,
    "tun": true,
    "tun_health": true,
    "tun_repair": true,
    "rule_updates": true,
    "geodata_updates": true,
    "core_update": false,
    "ui_update": false,
    "self_restart": false,
    "shutdown": true,
    "unified_update_scope": "rules_and_geodata"
  }
}
```

`state` is `loading`, `running`, or `stopping`. `running` is published only after
the full configuration application returns successfully, including TUN errors.
An apply failure leaves `loading` until the supervisor successfully reapplies
a valid configuration; it does not promise an automatic rollback. A rejected
parse or admission check preserves the previous readiness. Shutdown always
takes precedence. The supervisor also checks the actual HTTP/SOCKS/mixed ports
from `GET /configs`, since those legacy listener factories report bind errors
through logs rather than returning them to the configuration executor.
TUN capability means the
implementation is present; it does not assert privileges, driver readiness or
successful capture of traffic. The GUI can remain open even if the core cannot
start a configuration and the endpoint is unavailable.

### Local health and resume recovery

Core 1.0.23 adds `tun_health` and `tun_repair`; 1.0.26 adds
`tun_recovery_operations`. These optional API 1 capabilities are advertised only
on Windows. Feature-detect them before use. They describe local ownership and
resources, not Internet connectivity or a remote proxy.

`GET /desktop/health` returns an authenticated `no-store` snapshot immediately.
One coalescing observer performs native inspection separately from HTTP readers.
A cached healthy observation expires after five seconds; `stale: true` is not
proof of process death. Normal underlay changes retain the core and TUN. A
representative fresh response is:

```json
{
  "managed": true,
  "api_version": 1,
  "state": "running",
  "pid": 1234,
  "core_instance": "4dc7146e4f1042fcbbab7d99c94c6421",
  "config_generation": 1,
  "tun_generation": 1,
  "creation_count": 1,
  "observed_at": "2026-11-07T12:00:00Z",
  "stale": false,
  "recovery": null,
  "tun": {
    "enabled": true,
    "state": "healthy",
    "device": "Mihomo",
    "index": 42,
    "identity": "0006008000000000",
    "repairable": false
  }
}
```

The existing TUN states remain `disabled`, `healthy`, `degraded`, `unavailable`
and `conflict`; `reason` explains an observed failure. `enabled` is the requested
TUN intent; `/configs.tun` reports the applied configuration and withdraws the
enabled flag before a native close. Name/index and the actual handle LUID must
agree. Address checks include exact prefixes, lifetimes and DAD readiness;
automatic routes include their expected next hops. Failed enumeration never
proves absence. The supervisor independently checks the child process and proxy
listeners. `config_generation` tracks effective requested TUN configuration changes, not
unrelated DNS/rule reloads. `creation_count` counts listener factory invocations
(including failures); `tun_generation` counts
successfully installed listeners. In-place address/route repair changes neither.

For new supervisors, `POST /desktop/recovery` accepts only:

```json
{"request_id":"client-unique-request-1","config_generation":1,"tun_generation":1}
```

A running operation returns HTTP 202 with `{"operation": {...}}`. Repeating the
same request ID and target returns the same operation, including when the first
response was lost. Reusing an ID for a different target or supplying superseded
generations returns HTTP 409. Malformed/unknown fields are rejected. Another
active operation or configuration mutation returns HTTP 503 and `Retry-After: 1`.
The request cannot supply a device name, replacement configuration or force flag.

`GET /desktop/recovery/{id}` returns the operation. Its fields are `id`,
`request_id`, `core_instance`, `config_generation`, `tun_generation`, `phase`,
`status`, `tun`, `started_at`, `deadline_at`, optional `finished_at` and `error`.
Phases are `running`, `succeeded`, `failed` and `cancelled`. Terminal status is
`healthy`, `repaired`, `unavailable`, `conflict` or `needs_restart` (the in-progress
status is `running`). The latest relevant operation is also in health.recovery.
The last 64 operations remain queryable for this process lifetime; records do not
survive a core replacement. Unknown/expired IDs return HTTP 404. Supervisors must
bind IDs and generation numbers to `core_instance`.

The worker serializes with configuration and close. Missing owned Windows
addresses/routes are added in place, then verified; foreign entries are never
flushed. A verified failed device can be rebuilt. A closed-old/failed-new result
remains an explicit failure, even with no listener pointer. Recovery does not
reload configuration, download providers or change the user's running intent.
The operation has one 30-second deadline. A blocked native call can outlive that
deadline; the published failure does not release its ownership or permit a second
constructor. Process shutdown is the supervisor's escalation boundary, and exit
must be confirmed before another core starts.

Legacy `POST /desktop/recover` retains the empty body/query contract and the old
result schema (`pid`, `tun`, `status`, optional `error`). It starts/coalesces the
same background work and returns HTTP 503 while pending. A lost terminal reply
can be read again; `needs_restart` survives until a later valid observation or
configuration supersedes it. A later resource failure on the same device can
start a new recovery. HTTP 409 before readiness/during shutdown and HTTP 503 busy
are observations, not evidence of process death. Unknown ownership never grants
permission to remove an adapter. Successful recovery refreshes LAN preparation.

### Shutdown

`POST /desktop/shutdown` requires an empty body and no query parameters. It
closes admission to configuration changes, flushes HTTP 202 with
`{"status":"stopping"}`, then wakes the daemon's existing graceful shutdown
and cleanup retry loop. Acknowledgement means shutdown was requested, not that
TUN cleanup or process exit has finished. The supervisor must wait for the
process to exit before replacing files or starting another instance. Its own
bounded recovery policy governs a failed cleanup or an unresponsive process.

## Configuration and updates

Configuration-file editing, full reload, scalar patches, proxy selection,
connection telemetry, rule/provider updates and GeoData updates remain
available. Saving YAML retains the existing save-versus-apply behavior: syntax
and basic types are checked on save; complete validation occurs on reload.
The user's file is not rewritten with temporary control credentials.

Core auto-update, UI auto-update and first-run UI downloads are disabled at the
updater boundary, including after configuration reload. `POST /restart`,
`POST /upgrade`, and `POST /upgrade/ui` return HTTP 409 with code
`desktop_managed`. The response directs the caller to the desktop owner.
`GET /upgrade/ui` reports unsupported with reason `desktop_managed` without
checking a remote UI source.

The existing empty `POST /upgrade/check` now updates only configured HTTP rule
providers and GeoData in managed mode. Its task/status contract is retained;
`kernel` and `ui` items have `status: "skipped"`,
`reason: "desktop_managed"`, and automatic updates disabled. The supervisor
coordinates signed desktop/UI/core program updates as a compatible release.
Standalone `POST /upgrade/check` remains unchanged.

Linux `transparent-proxy` and explicit DNS `tcp-syn` retain their existing
platform restrictions. Managed mode does not translate Linux routing/UID/nft
configuration into Windows settings or alter DNS forwarding algorithms.
