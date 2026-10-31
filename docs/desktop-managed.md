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

Core 1.0.23 adds `tun_health` and `tun_repair`, advertised only on Windows.
These are optional API 1 capabilities; a supervisor must feature-detect them
before using the new endpoints with an older core. They report local ownership
and resources, not Internet connectivity or remote proxy availability.

`GET /desktop/health` returns a fresh authenticated, `no-store` snapshot:

```json
{
  "managed": true,
  "api_version": 1,
  "state": "running",
  "pid": 1234,
  "tun": {
    "enabled": true,
    "state": "healthy",
    "device": "Mihomo",
    "index": 42,
    "repairable": false
  }
}
```

TUN states are `disabled`, `healthy`, `degraded`, `unavailable`, or `conflict`;
an optional `reason` describes the local failure. The check compares the owned
device's name, index and Windows LUID, every configured address, and expected
automatic route prefixes with their gateway. An inspection error is not proof
that the interface is missing. The supervisor still checks its child process
identity and the actual configured proxy listeners.

`POST /desktop/recover` accepts an empty body and no query parameters. It
serializes with configuration changes, retains a healthy TUN, and recreates a
verified degraded TUN using the current applied configuration. It does not
download resources, reset the configuration, probe Internet reachability or
change the user's running intent. The result includes `pid`, `tun`, `status`
and an optional `error`; status is `healthy`, `repaired`, `unavailable`,
`conflict`, or `needs_restart`. The last result permits the supervisor to use
its bounded owned-process restart policy after confirmed shutdown. Foreign
device identities and unknown inspection failures do not authorize that path.

Both endpoints return HTTP 503 with `Retry-After: 1` when configuration or
recovery already owns the lifecycle lock. This is a retryable busy state, not
process death. A recover request before readiness or during shutdown returns
HTTP 409. Stop intent takes precedence over subsequent resume work. Successful
recovery also invalidates old LAN preparation results so current LAN intents
can prepare in the background.

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
