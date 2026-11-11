#!/usr/bin/env python3
"""Exercise independent automatic core replacement in the installed Windows app.

The elevated fixture seeds a checksum-pinned, published 1.0.28 executable only
while the GUI, host, core and service are all absent. The real medium-integrity
GUI then owns its ordinary host, whose installed service downloads the official
latest core. No update command, source override or replacement test hook is used.
Running, real TUN and stopped scenarios must retain their configuration and user
intent. This script only runs on a disposable elevated GitHub Windows runner.
"""

from __future__ import annotations

import ctypes
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import zipfile

from windows_smoke import (
    Acceptance, RestrictedDesktop, SmokeFailure, SocksFixture, TUN_BODY,
    TUN_TARGET, child_environment, free_port, listening, profile, require,
    safe_code, service_installed, wait_until,
)


BASELINE_VERSION = "mihomo-optimized-v1.0.28"
BASELINE_SOURCE = "e348a0f888b1ae497fb45c87a769bb479ca37e1c"
BASELINE_ARCHIVE = BASELINE_VERSION + "-windows-amd64.zip"
BASELINE_SHA256 = "6c213cc5ff1755900956711225dfefd55109339e9b6aa170167b6198054e4f49"
RELEASE_ROOT = "https://github.com/chummumm/mihomo-optimized/releases/download/"
ARCHIVE_MEMBER = "mihomo-optimized-windows-amd64.exe"
MINIMUM_UPDATED_VERSION = (1, 0, 29)
INTERVAL_HOURS = 24
UPDATE_TIMEOUT = 660


def file_hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def executable_version(path: Path) -> str:
    result = subprocess.run([str(path), "-v"], cwd=path.parent,
        env=child_environment(), stdin=subprocess.DEVNULL, capture_output=True,
        timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
    require(result.returncode == 0 and len(result.stdout) <= 16384
            and len(result.stderr) <= 16384, "core_fixture_version_probe_failed")
    output = result.stdout.decode("utf-8", errors="strict")
    match = re.search(r"\b(mihomo-optimized-v[0-9]+\.[0-9]+\.[0-9]+(?:-desktop)?)\b", output)
    require(match, "core_fixture_version_invalid")
    return match[1]


def download_fixture(name: str, destination: Path, limit: int) -> None:
    # Both the release and asset name are fixed in this file. Credentials and
    # ambient proxy overrides never reach curl or the actual application.
    require(name in (BASELINE_ARCHIVE, "SHA256SUMS"), "core_fixture_asset_invalid")
    url = RELEASE_ROOT + BASELINE_VERSION + "/" + name
    result = subprocess.run(["curl.exe", "--fail", "--location", "--silent", "--show-error",
        "--proto", "=https", "--proto-redir", "=https", "--connect-timeout", "20",
        "--max-time", "120", "--retry", "2", "--retry-delay", "2", "--retry-all-errors",
        "--max-filesize", str(limit), "--output", str(destination), url],
        env=child_environment(), stdin=subprocess.DEVNULL, capture_output=True,
        timeout=390, creationflags=subprocess.CREATE_NO_WINDOW)
    require(result.returncode == 0 and destination.is_file()
            and 0 < destination.stat().st_size <= limit, "core_fixture_download_failed")


class CoreUpdateAcceptance(Acceptance):
    def __init__(self, app: Path, artifacts: Path) -> None:
        super().__init__(app, artifacts)
        self.temporary: tempfile.TemporaryDirectory | None = None
        self.original_core: Path | None = None
        self.original_core_hash: str | None = None
        self.baseline_core: Path | None = None
        self.baseline_core_hash: str | None = None
        self.core_replaced = False
        self.report_path = artifacts.parent / "core-update.json"
        self.report["baseline"] = {"version": BASELINE_VERSION, "source": BASELINE_SOURCE,
            "url": RELEASE_ROOT + BASELINE_VERSION + "/" + BASELINE_ARCHIVE,
            "archiveSha256": BASELINE_SHA256}
        self.report["scenarios"] = {}

    def checkpoint(self) -> None:
        content = json.dumps(self.report, indent=2) + "\n"
        require(all(secret not in content for secret in self.secrets), "core_update_report_contains_secret")
        temporary = self.report_path.with_suffix(".json.tmp")
        temporary.write_text(content, encoding="utf-8")
        temporary.replace(self.report_path)

    def mark(self, name: str) -> None:
        super().mark(name)
        self.checkpoint()

    def update_files(self) -> list[Path]:
        return [self.core.with_name(self.core.name + ".previous"),
                self.core.with_name(self.core.name + ".update.json"),
                *self.core.parent.glob(".mihomo-core-update-*")]

    def require_idle_installation(self) -> None:
        require(not service_installed(), "core_fixture_service_still_installed")
        require(not self.processes.at_paths([self.app, self.host, self.core, self.helper]),
                "core_fixture_process_still_running")

    def prepare_baseline(self) -> None:
        self.stage = "baseline_download"
        self.temporary = tempfile.TemporaryDirectory(prefix="mihomo-core-update-smoke-")
        directory = Path(self.temporary.name)
        self.original_core = directory / "original-core.exe"
        shutil.copyfile(self.core, self.original_core)
        self.original_core_hash = file_hash(self.original_core)
        archive = directory / BASELINE_ARCHIVE
        manifest = directory / "SHA256SUMS"
        download_fixture("SHA256SUMS", manifest, 1024 * 1024)
        entries = [match[1].lower() for line in manifest.read_text(encoding="utf-8").splitlines()
                   if (match := re.fullmatch(r"([0-9a-fA-F]{64}) [ *]" + re.escape(BASELINE_ARCHIVE), line))]
        require(entries == [BASELINE_SHA256], "core_fixture_published_checksum_changed")
        download_fixture(BASELINE_ARCHIVE, archive, 64 * 1024 * 1024)
        require(file_hash(archive) == BASELINE_SHA256, "core_fixture_archive_checksum_mismatch")
        self.baseline_core = directory / "baseline-core.exe"
        with zipfile.ZipFile(archive) as package:
            entries = package.infolist()
            require(len(entries) == 1, "core_fixture_archive_members_invalid")
            entry = entries[0]
            require(entry.filename == ARCHIVE_MEMBER and not entry.is_dir()
                    and not stat.S_ISLNK(entry.external_attr >> 16)
                    and 0 < entry.file_size <= 256 * 1024 * 1024,
                    "core_fixture_archive_executable_invalid")
            with package.open(entry) as source, self.baseline_core.open("xb") as destination:
                shutil.copyfileobj(source, destination, 1024 * 1024)
            require(self.baseline_core.stat().st_size == entry.file_size,
                    "core_fixture_archive_size_mismatch")
        require(executable_version(self.baseline_core) == BASELINE_VERSION,
                "core_fixture_baseline_version_mismatch")
        self.baseline_core_hash = file_hash(self.baseline_core)
        self.report["baseline"]["executableSha256"] = self.baseline_core_hash
        self.mark("published_baseline_verified")

    def verify_protected_installation(self) -> None:
        import pywintypes
        import win32con
        import win32file
        import win32security

        # Opening existing objects for an access check never writes their data.
        # Check both the executable and its parent: replacing it cannot be done
        # by the medium-integrity UI through delete/add-child rights either.
        probes = [(self.core, win32con.GENERIC_WRITE, 0),
                  (self.core, win32con.DELETE, 0),
                  (self.core.parent, 0x0002, win32con.FILE_FLAG_BACKUP_SEMANTICS),
                  (self.core.parent, 0x0040, win32con.FILE_FLAG_BACKUP_SEMANTICS)]
        win32security.ImpersonateLoggedOnUser(self.desktop.token)
        try:
            for path, access, flags in probes:
                try:
                    handle = win32file.CreateFile(str(path), access,
                        win32con.FILE_SHARE_READ | win32con.FILE_SHARE_WRITE | win32con.FILE_SHARE_DELETE,
                        None, win32con.OPEN_EXISTING, flags, None)
                except pywintypes.error as error:
                    require(error.winerror == 5, "core_installation_access_probe_failed")
                else:
                    handle.Close()
                    raise SmokeFailure("core_installation_writable_by_gui")
        finally:
            win32security.RevertToSelf()

    def close_scenario(self) -> None:
        if self.driver:
            self.capture_children()
            self.driver.invoke("desktop_stop", timeout=35)
            self.driver.invoke("desktop_exit", {"force": True}, timeout=25)
            require(self.driver.process.wait(timeout=15) == 0, "core_update_gui_exit_failed")
            self.driver.close()
            self.driver = None
            self.gui = None
        wait_until(lambda: not self.processes.at_paths([self.app, self.host, self.core]),
                   "core_update_scenario_process_survived", 20)
        if self.service_owned:
            self.service_action("uninstall")
            wait_until(lambda: not service_installed() and not self.processes.at_paths([self.helper]),
                       "core_update_scenario_service_survived", 20)
            self.service_owned = False
        self.require_idle_installation()
        require(all(not path.exists() for path in self.update_files()),
                "core_update_transaction_not_cleaned")

    def seed_scenario(self, name: str, *, running: bool, tun: bool) -> tuple[dict, int, str]:
        self.stage = name + "_prepare"
        self.require_idle_installation()
        require(all(not path.exists() for path in self.update_files()), "core_fixture_update_already_pending")
        self.core_replaced = True
        # Copy into the existing file so the installed DACL is retained. The
        # entire old service/host session was confirmed absent above.
        shutil.copyfile(self.baseline_core, self.core)
        require(file_hash(self.core) == self.baseline_core_hash
                and executable_version(self.core) == BASELINE_VERSION, "core_fixture_seed_failed")
        self.verify_protected_installation()
        mixed = free_port()
        self.ports.add(mixed)
        device = "MihomoCoreUpdate" + ("Tun" if tun else "Proxy")
        content = profile(mixed, self.socks.server_address[1]).replace("MihomoSmokeTun", device)
        # Resolve official release hosts through the runner's working OS DNS;
        # the fixture must not depend on outbound UDP to a public DNS server.
        content = content.replace("nameserver: [1.1.1.1]", "nameserver: [system]")
        content += f"core-auto-update: {'false' if running else 'true'}\ncore-update-interval: {INTERVAL_HOURS}\n"
        (self.directory / "config.yaml").write_text(content, encoding="utf-8", newline="")
        (self.directory / "preferences.json").write_text(json.dumps({
            "desiredRunning": running, "systemProxy": False, "tun": tun}), encoding="utf-8")
        row = {"runningIntent": running, "tunIntent": tun, "baselineVersion": BASELINE_VERSION,
               "baselineSha256": self.baseline_core_hash, "observations": [],
               "manualUpdateCommands": 0, "trigger": "applied_yaml" if running else "startup_yaml"}
        self.report["scenarios"][name] = row
        self.service_owned = True  # Cleanup also covers a partly failed install.
        self.service_action("install")
        wait_until(service_installed, "core_update_service_install_timeout", 15)
        row["service"] = self.service_process_access()
        row["guiToken"] = self.launch_gui(self.artifacts / name)
        self.checkpoint()
        return row, mixed, content

    def observe_intent(self, *, running: bool, tun: bool) -> dict:
        status = self.status(timeout=8)
        require(status.get("desiredRunning") is running and status.get("tun") is tun
                and status.get("systemProxy") is False, "core_update_user_intent_changed")
        preferences = json.loads((self.directory / "preferences.json").read_text(encoding="utf-8"))
        require(preferences.get("desiredRunning") is running and preferences.get("tun") is tun
                and preferences.get("systemProxy") is False, "core_update_persisted_intent_changed")
        return status

    def await_automatic_update(self, row: dict, mixed: int, *, running: bool, tun: bool) -> dict:
        started = time.monotonic()
        while time.monotonic() - started < UPDATE_TIMEOUT:
            item = self.driver.invoke("desktop_core_update_status", timeout=8)
            require(isinstance(item, dict) and item.get("id") == "kernel" and item.get("kind") == "kernel",
                    "core_update_status_invalid")
            require(item.get("auto_update") is True and item.get("update_interval") == INTERVAL_HOURS
                    and item.get("interval_unit") == "hours", "core_update_applied_policy_missing")
            observation = {key: item.get(key) for key in ("status", "request_id", "current_version",
                "latest_version", "auto_update_state", "reason")}
            # No free-form host error or controller credential enters artifacts.
            require(all(value is None or isinstance(value, str) and re.fullmatch(r"[a-zA-Z0-9_.-]{0,100}", value)
                        for value in observation.values()), "core_update_status_fields_invalid")
            if not row["observations"] or row["observations"][-1] != observation:
                row["observations"].append(observation)
                self.checkpoint()
            if item.get("request_id"):
                require(re.fullmatch(r"automatic-[0-9]+", item["request_id"]),
                        "core_update_was_not_automatic")
            if not running:
                status = self.observe_intent(running=False, tun=tun)
                require(status.get("phase") == "stopped" and status.get("controller") is None
                        and not listening(mixed), "core_update_started_stopped_core")
            if item.get("status") in ("error", "up_to_date") or (item.get("status") == "skipped" and item.get("reason")):
                raise SmokeFailure("automatic_core_update_" + safe_code(item.get("reason") or item["status"]))
            if item.get("status") == "updated":
                version = item.get("current_version", "")
                match = re.fullmatch(r"mihomo-optimized-v([0-9]+)\.([0-9]+)\.([0-9]+)", version)
                require(match and tuple(map(int, match.groups())) >= MINIMUM_UPDATED_VERSION
                        and item.get("latest_version") == version, "core_update_latest_version_invalid")
                require(item.get("auto_update_state") == "scheduled"
                        and type(item.get("next_check_in_ms")) is int
                        and 0 < item["next_check_in_ms"] <= INTERVAL_HOURS * 3600000,
                        "core_update_next_check_not_scheduled")
                row["durationSeconds"] = round(time.monotonic() - started, 2)
                row["updatedVersion"] = version
                row["nextCheckInMilliseconds"] = item["next_check_in_ms"]
                return item
            time.sleep(0.5)
        raise SmokeFailure("automatic_core_update_timeout")

    def proxy_roundtrip(self, mixed: int) -> bool:
        connection = http.client.HTTPConnection("127.0.0.1", mixed, timeout=3)
        try:
            connection.request("GET", f"http://{TUN_TARGET}/mihomo-tun-smoke", headers={"Connection": "close"})
            response = connection.getresponse()
            return response.status == 200 and response.read(4096) == TUN_BODY
        except (OSError, http.client.HTTPException):
            return False
        finally:
            connection.close()

    def verify_core_row(self, row: dict) -> None:
        # Navigation happens only after the automatic task completes: visiting
        # the resources page cannot be responsible for scheduling the update.
        self.driver.navigate("profiles")
        scrolling = self.driver.ui("ProfilesScrollViewer")
        if scrolling.get("scroll", {}).get("verticalScrollable"):
            self.driver.ui("ProfilesScrollViewer", "scroll", "100")
        def current_version_visible():
            field = self.driver.ui("CoreUpdateCurrentVersion")
            return field if field.get("found") and not field.get("offscreen") and field.get("name") == row["updatedVersion"] else None
        wait_until(current_version_visible, "core_update_version_row_missing", 20)
        latest = self.driver.ui("CoreUpdateLatestVersion")
        policy = self.driver.ui("CoreUpdatePolicy")
        schedule = self.driver.ui("CoreUpdateSchedule")
        status = self.driver.ui("CoreUpdateStatus")
        require(latest.get("found") and row["updatedVersion"] in latest.get("name", "")
                and policy.get("found") and str(INTERVAL_HOURS) in policy.get("name", "")
                and schedule.get("found") and re.search(r"[0-9]{4}-[0-9]{2}-[0-9]{2}", schedule.get("name", ""))
                and status.get("found") and status.get("name") in ("已更新", "Updated"),
                "core_update_policy_row_incomplete")
        row["normalResourcePageShowsCoreState"] = True

    def scenario(self, name: str, *, running: bool, tun: bool) -> None:
        row, mixed, content = self.seed_scenario(name, running=running, tun=tun)
        self.stage = name
        previous_pid = None
        if running:
            before = self.phase("running")
            self.observe_intent(running=True, tun=tun)
            previous_pid = self.managed_core(before)["pid"]
            require(self.runtime_json("version").get("version") == BASELINE_VERSION,
                    "core_update_baseline_not_running")
            task = self.driver.invoke("desktop_core_update_status")
            require(task.get("auto_update") is False and not task.get("request_id"),
                    "core_update_disabled_policy_started_job")
            forward = self.socks.roundtrip if tun else lambda: self.proxy_roundtrip(mixed)
            wait_until(forward, "core_update_baseline_forwarding_failed", 20)
            content = content.replace("core-auto-update: false\n", "core-auto-update: true\n")
            self.save_configuration(content)
            # The ordinary save/reload flow is the only trigger. Never call
            # desktop_update_core (directly or through the UI) in this test.
            self.driver.invoke("desktop_reload_config", {"force": True})
        source = (self.directory / "config.yaml").read_bytes()
        require(source == content.encode("utf-8"), "core_update_config_apply_changed_source")
        item = self.await_automatic_update(row, mixed, running=running, tun=tun)
        require((self.directory / "config.yaml").read_bytes() == source,
                "core_update_changed_yaml")
        require(executable_version(self.core) == item["current_version"], "core_update_disk_version_mismatch")
        updated_hash = file_hash(self.core)
        require(updated_hash != self.baseline_core_hash, "core_update_binary_not_replaced")
        self.verify_protected_installation()
        status = self.observe_intent(running=running, tun=tun)
        if running:
            require(status.get("phase") == "running", "core_update_running_intent_not_restored")
            current_pid = self.managed_core(status)["pid"]
            require(current_pid != previous_pid and self.runtime_json("version").get("version") == item["current_version"],
                    "core_update_runtime_version_mismatch")
            configuration = self.runtime_json("configs")
            require(configuration.get("tun", {}).get("enable") is tun, "core_update_runtime_tun_changed")
            if tun:
                require(status.get("service", {}).get("running") is True
                        and configuration["tun"].get("device") == "MihomoCoreUpdateTun",
                        "core_update_tun_service_not_restored")
            else:
                row["updatedCoreToken"] = self.desktop.check_gui(
                    self.processes.identity(current_pid), self.processes)
            wait_until(self.socks.roundtrip if tun else lambda: self.proxy_roundtrip(mixed),
                       "core_update_forwarding_not_restored", 25)
            row["previousPid"] = previous_pid
            row["updatedPid"] = current_pid
            row["forwarding"] = "raw_socket_via_real_tun" if tun else "http_proxy_via_real_core"
        else:
            # Check again after background polls have had time to run. Version
            # validation probes are allowed; no managed runtime may be started.
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                status = self.observe_intent(running=False, tun=tun)
                require(status.get("phase") == "stopped" and status.get("controller") is None
                        and not self.processes.at_paths([self.core]) and not listening(mixed),
                        "core_update_delayed_start_after_stopped_update")
                time.sleep(0.25)
            row["stayedStopped"] = True
        row["yamlSha256"] = hashlib.sha256(source).hexdigest()
        row["updatedSha256"] = updated_hash
        row["yamlUnchanged"] = True
        row["intentPreserved"] = True
        self.verify_core_row(row)
        if running and tun:
            # The SYSTEM service installed these bytes. A later ordinary
            # (medium-integrity) start must still have read/execute permission.
            self.driver.invoke("desktop_set_tun", {"enabled": False})
            direct = self.phase("running")
            self.observe_intent(running=True, tun=False)
            direct_pid = self.managed_core(direct)["pid"]
            require(direct_pid != current_pid
                    and self.runtime_json("version").get("version") == item["current_version"]
                    and self.runtime_json("configs").get("tun", {}).get("enable") is False,
                    "core_update_tun_to_direct_start_failed")
            row["tunToDirectCoreToken"] = self.desktop.check_gui(
                self.processes.identity(direct_pid), self.processes)
            wait_until(lambda: self.proxy_roundtrip(mixed), "core_update_tun_to_direct_forwarding_failed", 20)
            require((self.directory / "config.yaml").read_bytes() == source
                    and file_hash(self.core) == updated_hash, "core_update_tun_to_direct_changed_source")
            row["tunToDirectVerified"] = True
        self.mark(name)
        self.close_scenario()

    def run(self) -> None:
        require(self.core.is_file() and self.host.is_file() and self.helper.is_file(), "installed_sidecars_missing")
        self.require_idle_installation()
        require(all(not path.exists() for path in self.update_files()), "preexisting_core_update_transaction")
        require(not (self.directory / "system-proxy-recovery.json").exists(), "preexisting_proxy_recovery")
        self.directory.mkdir(parents=True, exist_ok=True)
        for name in ("config.yaml", "config.yaml.bak", "preferences.json", "native-appearance.json", "native-proxies.json", "native-shell.json"):
            path = self.directory / name
            self.original_files[path] = path.read_bytes() if path.exists() else None
        self.mutations_started = True
        self.wininet.restore({"flags": 1, "server": "", "bypass": "", "pac": ""})
        self.proxy_disabled = self.wininet.read()
        self.desktop = RestrictedDesktop()
        self.socks = SocksFixture()
        sidecars = {path: file_hash(path) for path in (self.app, self.host, self.helper)}
        self.prepare_baseline()
        self.scenario("running_auto_update", running=True, tun=False)
        self.scenario("tun_auto_update", running=True, tun=True)
        self.scenario("stopped_auto_update", running=False, tun=True)
        require(all(file_hash(path) == checksum for path, checksum in sidecars.items()),
                "core_update_replaced_desktop_application")
        self.mark("desktop_application_unchanged")

    def cleanup(self) -> None:
        super().cleanup()
        try:
            if self.core_replaced:
                self.require_idle_installation()
                # Preflight excluded all these transaction paths. Only files
                # created by this test's real updater are removed after every
                # owning process has exited; the original bundle is restored.
                for path in self.update_files():
                    if path.is_dir():
                        shutil.rmtree(path)
                    else:
                        path.unlink(missing_ok=True)
                shutil.copyfile(self.original_core, self.core)
                require(file_hash(self.core) == self.original_core_hash, "core_bundle_restore_failed")
                self.report["tests"]["original_bundle_restored"] = True
            if self.temporary:
                self.temporary.cleanup()
        except Exception as error:
            code = safe_code(str(error)) if isinstance(error, SmokeFailure) else "unexpected_" + type(error).__name__
            self.report["diagnostics"].append({"stage": "core_bundle_cleanup", "code": code})
            self.report["tests"]["original_bundle_restored"] = False


def main() -> int:
    artifacts = Path("artifacts/verification/core-update")
    acceptance = None
    allowed = sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
    try:
        require(allowed, "disposable_github_windows_runner_required")
        require(ctypes.windll.shell32.IsUserAnAdmin(), "elevated_runner_required_for_core_fixture")
        artifacts.mkdir(parents=True, exist_ok=True)
        app = Path(os.environ.get("MIHOMO_TEST_APP", "")).resolve(strict=True)
        expected = (Path(os.environ["ProgramFiles"]) / "Mihomo Optimized" / "mihomo-optimized.exe").resolve()
        require(app == expected and app.is_file(), "installed_application_missing")
        acceptance = CoreUpdateAcceptance(app, artifacts)
        acceptance.run()
    except Exception as error:
        code = safe_code(str(error)) if isinstance(error, SmokeFailure) else "unexpected_" + type(error).__name__
        print("FAIL: " + code, file=sys.stderr, flush=True)
        diagnostic = {"stage": acceptance.stage if acceptance else "preflight", "code": code}
        if isinstance(error, SmokeFailure) and error.uia:
            diagnostic["uia"] = error.uia
        if not isinstance(error, SmokeFailure):
            import traceback
            diagnostic["frames"] = [{"function": safe_code(frame.name), "line": frame.lineno}
                for frame in traceback.extract_tb(error.__traceback__)
                if Path(frame.filename).name == Path(__file__).name][-8:]
            for key in ("winerror", "errno"):
                number = getattr(error, key, None)
                if isinstance(number, int):
                    diagnostic[key] = number
        if acceptance:
            acceptance.report["diagnostics"].append(diagnostic)
        elif allowed:
            artifacts.parent.mkdir(parents=True, exist_ok=True)
            (artifacts.parent / "core-update.json").write_text(json.dumps({
                "passed": False, "platform": "windows", "tests": {}, "diagnostics": [diagnostic],
            }, indent=2) + "\n", encoding="utf-8")
    finally:
        if acceptance:
            try:
                acceptance.cleanup()
            except Exception as error:
                acceptance.report["diagnostics"].append({"stage": "cleanup", "code": "unexpected_" + type(error).__name__})
            required = {"published_baseline_verified", "running_auto_update", "tun_auto_update",
                        "stopped_auto_update", "desktop_application_unchanged", "final_cleanup", "original_bundle_restored"}
            acceptance.report["passed"] = all(acceptance.report["tests"].get(name) is True for name in required) and not acceptance.report["diagnostics"]
            acceptance.checkpoint()
    return 0 if acceptance and acceptance.report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
