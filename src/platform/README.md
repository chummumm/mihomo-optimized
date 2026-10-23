# Legacy Tauri UI adapter

The Windows application from 0.2.0 uses the C# WPF project in `src-windows`.
Its development, packaging and acceptance instructions are in
[`desktop/README.zh-CN.md`](../../desktop/README.zh-CN.md). The adapter described
below is retained for historical web fixtures; it is not part of the native
Windows package. `pnpm desktop:dev` and `pnpm build:desktop` now build the WPF
application. The explicitly named `desktop-legacy` commands below only exercise
the former web adapter.

The former Windows build used the existing React dashboard, routes, components, themes,
and translations. `vite --mode desktop` defines `__DESKTOP__` and omits the PWA
plugin. `pnpm build` still produces the browser dashboard with its existing
controller selection and PWA support; `pnpm build:desktop-legacy` writes `dist-desktop`.

## Boundary and ownership

- `model.ts` defines and validates the native status contract. A running core
  must supply an HTTP controller at `127.0.0.1` with an explicit port and secret.
- `desktop.ts` owns IPC, events, actual host status, visibility, and draft/exit
  protection. It uses the official Tauri v2 API with dynamic imports.
- `DesktopUI.tsx` adds local core controls, recovery, configuration import, the
  application updater, and an exit confirmation to the existing UI. Native
  configuration dialogs live outside the route outlet so navigation also keeps
  their drafts and in-flight writes alive.
- `stores/request.ts` ignores URL and localStorage controller settings in desktop
  builds. The native credential lives only in memory. Inactive HTTP clients are
  blocked before sending requests. A separate stable configuration client keeps
  an open editor and its draft alive across core stops, restarts, and failures.
- `lib/request.ts` routes persistent configuration operations through native IPC.
  Ordinary dashboard reads and controls retain the authenticated core HTTP/WS
  API. The desktop UI never reads or writes the generated runtime YAML.

The host implementation lives in `src-tauri/src/commands.rs` and `desktop.rs`;
the verified application updater lives in `src-tauri/src/update.rs`. These IPC
commands are only hosted by the Windows application.

## IPC contract

Arguments use JavaScript camelCase. Host failures are sanitized `{ code,
message }` objects; unexpected error strings are replaced with a generic error.

| Commands | Arguments and result |
| --- | --- |
| `desktop_status` | Returns `DesktopStatus`. |
| `desktop_start`, `desktop_stop`, `desktop_restart` | Return confirmed `DesktopStatus`. |
| `desktop_set_system_proxy`, `desktop_set_tun`, `desktop_set_autostart` | `{ enabled: boolean }`; return `DesktopStatus`. |
| `desktop_install_service`, `desktop_uninstall_service` | Return `DesktopStatus`. |
| `desktop_read_config` | Returns `DesktopConfigDocument`. |
| `desktop_save_config` | `{ content, expectedRevision }`; validates and saves the main YAML, returns its new document/revision. |
| `desktop_import_config` | `{ content, expectedRevision }`; saves an imported YAML and backs up the prior file. |
| `desktop_import_config_url` | `{ url, expectedRevision }`; native HTTP/HTTPS download, then the same import. |
| `desktop_reload_config` | `{ force: boolean }`; applies the saved configuration when running, otherwise validates without starting the core. |
| `desktop_patch_config` | `{ patch }`; persists allowed port, mode, LAN, and log settings, then reloads. |
| `desktop_set_dirty` | `{ dirty: boolean }`; protects tray exit and application installation. |
| `desktop_exit` | Optional `{ force: boolean }`; dirty exit requests require UI confirmation before `force: true`. |
| `desktop_open_directory` | `{ kind: 'config' \| 'logs' }`. |
| `desktop_open_external` | `{ url }`; opens an HTTP/HTTPS link in the system browser. |
| `desktop_check_update` | Returns `{ currentVersion, available, canInstall, releaseUrl?, version?, publishedAt?, body? }`. Portable apps offer the full release download when `canInstall` is false. |
| `desktop_install_update` | Downloads, verifies, and launches the application installer through the host. Requires the protected installed application path. |

`DesktopStatus` contains `appVersion`, `phase`, `systemProxy`, `tun`, `autostart`,
`controller`, `lastError`, `coreVersion`, `configPath`, `logPath`, `service`, and
`capabilities` (see `model.ts`). The system proxy and autostart switches reflect
confirmed host state. `tun` is the selected core mode, so it can be chosen while
stopped; the top status bar shows TUN active only while that mode is running.
Service installation/UAC and rollback belong to the host. The portable app uses
the regular core mode; service/TUN mode requires the protected installed app.

`DesktopConfigDocument` has `{ path, content, revision, max_size, writable }`.
The 4 MiB limit, revision conflict detection, read-only checks, YAML validation,
atomic replacement, and the import backup `config.yaml.bak` are enforced by the
host. Save/import and reload are separate: a successful save followed by a failed
reload stays saved and is reported as such. URL imports accept a complete UTF-8
YAML configuration, not an arbitrary node list or subscription conversion.

## Events and lifecycle

- `desktop-status`: validated `DesktopStatus`; supersedes in-flight older reads.
- `desktop-visibility`: `{ visible }`; pauses connection, throughput, DNS, log,
  topology, and update observation through `lib/activity.ts`, even if WebView2
  still reports the document as visible. Showing the window reconnects readers.
- `desktop-exit-requested`: opens a discard/keep-editing dialog. An in-flight
  configuration write must finish before discard-and-exit becomes available.
- `desktop-update-progress`: `{ stage, downloaded?, total?, message? }`, with
  `checking`, `downloading`, `installing`, `ready`, or `error` stages.
- The host also emits `desktop-config-changed { revision }`. The UI does not
  auto-replace an editor draft; saves use the original revision and surface a
  conflict if another operation changed the file.

The application renders recovery controls before startup IPC completes. It does
not independently auto-start the core: the host restores its running preference.
System proxy/autostart/TUN availability comes from host capabilities, without
impersonating ClashX. Browser navigation to an external website is handed to the
host; the WebView remains on the bundled interface.

The app, sidecar core, and bundled dashboard update as one application. The
existing resource update panel remains available for rules and geographic data,
with resource-specific controls and results, and kernel/UI self-update rows
hidden. When the core is stopped, its resource panel explains why updating is
disabled; application updates remain available. Configuration must be saved or
discarded before installing an application update.

The desktop status bar owns the single start/stop control. Settings retains
restart and read-only status refresh, and groups its one source-file editor with
reload of the saved configuration. The browser controller card remains a web
feature; desktop connections are host-managed. Bootstrap download options use
the same YAML editor and source file, without a second configuration store.

## Startup update checks

The desktop app schedules at most one background `desktop_check_update` per
application startup. It waits five seconds after initialization and defers while the
core is starting or stopping, or a desktop operation or configuration write is
in progress. The check stays pending without a polling timer or an expiry; a
stable status after successful or failed preparation resumes it. A prior manual
check consumes the startup opportunity, and confirmed exit or WebView unload
cancels a pending check. Route changes and showing the tray window again do not
schedule another check after it has been consumed. A newer
version produces a dismissible notice; settings retain the update result and
provide manual checks and retry.

Checking fetches release information only. Downloads and installation begin
after the user chooses to install an update. Portable apps instead open the
verified release URL through `desktop_open_external`, so the user can download
the full installer or a new portable archive. Existing dirty-configuration and
native installation checks still apply.

A failed or timed-out background check does not stop or restart the core,
change system proxy settings, or block the startup recovery controls. The error
belongs to the update UI, and the user can retry from settings after connectivity
returns. The browser dashboard keeps its existing update behavior.

## Verification

```sh
pnpm install --frozen-lockfile
pnpm typecheck
pnpm test:unit
pnpm build
pnpm test:browser
pnpm build:desktop-legacy
pnpm test:desktop-legacy
```

Browser tests use Playwright Chromium. Set `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH`
to use a preinstalled compatible Chromium. `UI_DIST_DIR` overrides the build
directory, and `DESKTOP_SCREENSHOT` optionally captures the desktop settings UI.

`model.test.cjs` tests status validation, controller isolation, and error
sanitization. `lib/connection-stream.test.cjs` covers native hiding independently
of browser visibility. `desktop.browser.cjs` runs the production desktop build
against a deterministic native IPC fixture and authenticated HTTP/WS server. It
covers offline editing, draft survival through core failure/restart, revision
conflicts, saved-file reload recovery, dirty exit, native settings/import/update,
file size rejection, URL/storage isolation, no PWA, and tray pause/resume. Update
coverage includes the single startup check, dismissing the notice, explicit
installation, manual retry after a failed background check, three-minute
preparation, active native operations/configuration writes, and exit
cancellation. It also checks single settings entries and resource-only update
messages while the original controller operations remain available on the web.
The original ten browser regression suites continue to exercise the web build.

These browser fixtures verify UI behavior and the IPC contract. Windows EXE,
WebView2, actual OS proxy restoration, UAC/service behavior, and installer/update
verification require the native tests and Windows acceptance workflow.
