"""Actual Windows TUN ownership, lifecycle and published-version acceptance.

The hot upgrade uses the installed published GUI, host, service and core. The
crash/foreign cases use verified private copies of the published and candidate
cores. No supervisor can restart those owners between exit and the candidate's
next operation. Tests preserve the configured name, never invoke a repair
command and never create/open a Wintun adapter just to inspect its identity.
"""

from __future__ import annotations

import contextlib
import ctypes
from ctypes import wintypes
import hashlib
import http.client
from datetime import datetime
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import stat
import subprocess
import tempfile
import time
from urllib.parse import urlsplit
import uuid

from upgrade_tun_native import NativeTunIdentity, expected_guid, same_identity
from windows_smoke import (
    NativeDriver, RestrictedDesktop, SmokeFailure, SocksFixture, TUN_BODY, TUN_TARGET, child_environment,
    free_port, profile, require, safe_code, wait_until,
)


OLD_CORE_SOURCE = "96c428bce6b1e7d68327910dad5f426ad29d2d98"
START_TIMEOUT = 60.0
DESKTOP_RESTART_CYCLES = 20
AUTO_RECOVERY_EVENTS = ("tun_device_auto_recovery", "tun_device_auto_recovery_succeeded")


def file_hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_controller_record(home: Path, pid: int, nonce: str) -> dict | None:
    """Discover a single managed launch; a record never substitutes for readiness."""
    path = home / "desktop-controller.json"
    try:
        metadata = path.lstat()
    except FileNotFoundError:
        return None
    require(stat.S_ISREG(metadata.st_mode) and not path.is_symlink()
            and not getattr(metadata, "st_file_attributes", 0) & 0x400,
            "upgrade_controller_record_not_regular")
    try:
        with path.open("rb") as stream:
            opened = os.fstat(stream.fileno())
            require(stat.S_ISREG(opened.st_mode)
                    and not getattr(opened, "st_file_attributes", 0) & 0x400,
                    "upgrade_controller_record_not_regular")
            payload = stream.read(4097)
    except FileNotFoundError:
        return None
    require(0 < len(payload) <= 4096, "upgrade_controller_record_size_invalid")

    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "upgrade_controller_record_duplicate_key")
            result[key] = value
        return result

    try:
        value = json.loads(payload, object_pairs_hook=unique_object)
    except (ValueError, UnicodeError):
        raise SmokeFailure("upgrade_controller_record_json_invalid") from None
    require(isinstance(value, dict) and set(value) == {
        "protocolVersion", "pid", "controller", "coreInstance", "launchNonce"},
        "upgrade_controller_record_fields_invalid")
    require(type(value["protocolVersion"]) is int and value["protocolVersion"] == 1
            and type(value["pid"]) is int and value["pid"] == pid and pid > 0
            and isinstance(nonce, str) and re.fullmatch(r"[0-9a-f]{64}", nonce)
            and value["launchNonce"] == nonce, "upgrade_controller_record_identity_invalid")
    address = value["controller"]
    match = re.fullmatch(r"127\.0\.0\.1:([1-9][0-9]{0,4})", address) if isinstance(address, str) else None
    require(match and int(match[1]) <= 65535, "upgrade_controller_record_address_invalid")
    require(isinstance(value["coreInstance"], str)
            and re.fullmatch(r"[a-zA-Z0-9_-]{1,128}", value["coreInstance"]),
            "upgrade_controller_record_instance_invalid")
    return value


class TunUpgradeAcceptance:
    def __init__(self, owner, baseline: Path, version: str, source: str) -> None:
        self.owner, self.baseline, self.old_version, self.old_source = owner, baseline, version, source
        self.native = NativeTunIdentity()
        self.preferences = owner.config.with_name("preferences.json")
        self.original_config = owner.config.read_bytes()
        self.original_preferences = self.preferences.read_bytes() if self.preferences.exists() else None
        self.desktop = None
        self.socks = None
        self.driver = None
        self.child = None
        self.child_output = None
        self.child_controller = None
        self.child_managed = False
        self.child_identity = None
        self.child_record = None
        self.children = []
        self.child_handles = {}
        self.adapters = []
        self.secrets = set()
        self.temp = tempfile.TemporaryDirectory(prefix="mihomo-upgrade-tun-")
        # Windows TEMP may contain an 8.3 component such as RUNNER~1. Use the
        # actual private directory spelling before copying or launching files.
        self.directory = Path(self.temp.name).resolve(strict=True)
        self.evidence = {"oldDesktopVersion": version, "oldDesktopSource": source,
                         "oldCoreSource": OLD_CORE_SOURCE, "candidateCoreSource": owner.info["coreCommit"],
                         "scenarios": {}, "cleanup": []}
        owner.report["nativeTunUpgrade"] = self.evidence

    def checkpoint(self) -> None:
        self.owner.write_report()

    def source(self, name: str, *, mixed: int | None = None) -> bytes:
        return ("# real-upgrade-tun-" + uuid.uuid4().hex + "\n"
                + profile(free_port() if mixed is None else mixed, self.socks.server_address[1]).replace(
                    "  device: MihomoSmokeTun\n", "  device: " + name + "\n")).encode("utf-8")

    def configure(self, content: bytes, *, tun: bool = True) -> None:
        require(self.driver is None, "upgrade_configure_requires_stopped_gui")
        self.owner.config.write_bytes(content)
        self.preferences.write_text(json.dumps({"desiredRunning": False, "systemProxy": False, "tun": tun}),
                                    encoding="utf-8")
        self.config_hash = file_hash(self.owner.config)

    def unchanged_source(self, evidence: dict, stage: str) -> None:
        actual = file_hash(self.owner.config)
        evidence.setdefault("sourceSha256ByStage", {})[stage] = actual
        self.checkpoint()
        require(actual == self.config_hash, "upgrade_tun_source_yaml_changed")

    def request(self, controller: dict, path: str, method: str = "GET") -> dict:
        expected_status = {("GET", "/desktop"): 200, ("GET", "/desktop/health"): 200, ("GET", "/configs"): 200,
                           ("GET", "/version"): 200, ("POST", "/desktop/shutdown"): 202}.get((method, path))
        require(expected_status is not None, "upgrade_controller_endpoint_invalid")
        url = urlsplit(controller.get("url", ""))
        secret = controller.get("secret", "")
        require(url.scheme == "http" and url.hostname == "127.0.0.1" and url.port
                and not url.username and not url.password and len(secret) >= 32,
                "upgrade_controller_identity_invalid")
        # Only allowlisted method/path, numeric status/size and body categories
        # enter the report. Never retain response data, headers, URLs or secrets.
        observation = {"method": method, "path": path, "expectedStatus": expected_status,
                       "status": None, "bytes": 0, "bodyKind": "unread", "accepted": False}
        history = self.evidence.setdefault("controllerResponses", [])
        history.append(observation)
        del history[:-64]
        connection = http.client.HTTPConnection("127.0.0.1", url.port, timeout=3)
        try:
            connection.request(method, path, headers={"Authorization": "Bearer " + secret, "Connection": "close"})
            response = connection.getresponse()
            content = response.read(1024 * 1024 + 1)
            observation.update({"status": response.status, "bytes": len(content)})
            value = None
            if len(content) > 1024 * 1024:
                observation["bodyKind"] = "oversized"
            elif secret.encode() in content:
                observation["bodyKind"] = "contains_secret"
            elif not content:
                observation["bodyKind"] = "empty"
            else:
                try:
                    value = json.loads(content)
                    observation["bodyKind"] = "json_object" if isinstance(value, dict) else "json_non_object"
                except (ValueError, UnicodeError):
                    observation["bodyKind"] = "invalid_json"
            require(response.status == expected_status, "upgrade_controller_response_status")
            require(observation["bodyKind"] == "json_object", "upgrade_controller_response_body")
            if (method, path) == ("POST", "/desktop/shutdown"):
                # Accepted acknowledges shutdown admission. The caller must
                # still wait on the exact process handle before any new Create.
                require(value == {"status": "stopping"}, "upgrade_controller_shutdown_ack_invalid")
            observation["accepted"] = True
            return value
        except (SmokeFailure, OSError, http.client.HTTPException) as error:
            observation["error"] = str(error) if isinstance(error, SmokeFailure) else type(error).__name__
            self.checkpoint()
            raise
        finally:
            connection.close()

    def status(self, timeout: float = 5) -> dict:
        status = self.driver.invoke("desktop_status", timeout=timeout)
        require(isinstance(status, dict), "upgrade_status_invalid")
        secret = (status.get("controller") or {}).get("secret")
        if isinstance(secret, str) and secret:
            self.secrets.add(secret)
        self.evidence["lastStatus"] = {key: status.get(key) for key in
            ("phase", "errorCode", "desiredRunning", "tun", "tunHealth", "tunDevice", "operation", "pid",
             "processState", "controlState", "listenersReady", "statusStale", "observedAt",
             "intentRevision", "statusSequence", "lastOperationFailure")}
        return status

    def recovery_cursor(self) -> dict:
        require(self.driver is None, "upgrade_recovery_cursor_requires_stopped_gui")
        path = self.owner.config.parent / "logs/recovery.log"
        if not path.exists():
            return {"offset": 0, "fileIdentity": None}
        stat = path.stat()
        # Capture only while this scenario's GUI/host is not running. A cursor
        # cannot start in the middle of a concurrent diagnostic record.
        with path.open("rb") as stream:
            if stat.st_size:
                stream.seek(stat.st_size - 1)
                require(stream.read(1) == b"\n", "upgrade_recovery_log_cursor_incomplete")
        return {"offset": stat.st_size, "fileIdentity": [stat.st_dev, stat.st_ino]}

    def recovery_events(self, cursor: dict) -> list[dict]:
        path = self.owner.config.parent / "logs/recovery.log"
        if not path.exists():
            require(cursor["offset"] == 0, "upgrade_recovery_log_disappeared")
            return []
        stat = path.stat()
        require(stat.st_size >= cursor["offset"] and (cursor["fileIdentity"] is None
                or cursor["fileIdentity"] == [stat.st_dev, stat.st_ino]), "upgrade_recovery_log_rotated")
        with path.open("rb") as stream:
            stream.seek(cursor["offset"])
            data = stream.read(256 * 1024 + 1)
        require(len(data) <= 256 * 1024, "upgrade_recovery_log_window_too_large")
        # A last write may still be in flight. Parse complete JSON lines only;
        # a malformed complete record is a failure, not ignored evidence.
        records = [json.loads(line) for line in data.split(b"\n")[:-1]]
        require(all(isinstance(record, dict) for record in records), "upgrade_recovery_log_record_invalid")
        return [record for record in records if record.get("event") in AUTO_RECOVERY_EVENTS]

    def assert_recovery_events(self, cursor: dict, expected: list[dict], evidence: dict, stage: str) -> None:
        actual = self.recovery_events(cursor)
        evidence.setdefault("automaticRecoveryEventCounts", {})[stage] = len(actual)
        self.checkpoint()
        require(actual == expected, "upgrade_unexpected_additional_automatic_recovery")

    @staticmethod
    def remaining(deadline: float) -> float:
        remaining = deadline - time.monotonic()
        require(remaining > 0, "upgrade_tun_start_budget_expired")
        return remaining

    def log_tail(self, stream) -> str:
        stream.seek(0, os.SEEK_END)
        stream.seek(max(0, stream.tell() - 8192))
        text = stream.read().decode("utf-8", errors="replace")
        for secret in self.secrets:
            text = text.replace(secret, "[redacted]")
        return "".join(char for char in text if char in "\n\t" or ord(char) >= 32)

    def capture_logs(self, label: str) -> None:
        # upgrade_smoke preflight required both data directories to be absent.
        # These bounded logs belong only to this generated, secret-free fixture.
        captured = {}
        for source, directory in (("user", self.owner.config.parent / "logs"),
                                  ("service", self.owner.owner_file.parent / "logs")):
            for name in ("core.log", "recovery.log"):
                path = directory / name
                if path.is_file():
                    with path.open("rb") as stream:
                        captured[source + "/" + name] = self.log_tail(stream)
        self.evidence.setdefault("fixtureLogs", {})[label] = captured
        self.checkpoint()

    def launch_gui(self, label: str) -> None:
        artifacts = self.owner.artifacts / ("upgrade-tun-" + label)
        artifacts.mkdir(parents=True, exist_ok=True)
        self.driver = NativeDriver(self.owner.app, self.owner.processes, self.desktop, artifacts)
        self.evidence.setdefault("guiTokens", {})[label] = self.desktop.check_gui(self.driver.identity, self.owner.processes)
        wait_until(lambda: self.status().get("service", {}).get("installed") is True,
                   "upgrade_gui_service_observation_timeout", 12)

    def start_gui(self, evidence: dict, label: str, *, resume_running_intent: bool = False,
                  expect_conflict: bool = False, deadline: float | None = None) -> dict:
        started = time.monotonic()
        deadline = deadline if deadline is not None else started + START_TIMEOUT
        record = {"startCommandCount": 0, "budgetMilliseconds": round((deadline - started) * 1000),
                  "observations": []}
        evidence.setdefault("starts", {})[label] = record

        def observe() -> dict:
            status = self.status(timeout=min(5, self.remaining(deadline)))
            value = {key: status.get(key) for key in ("phase", "operation", "errorCode", "pid", "intentRevision")}
            if not record["observations"] or record["observations"][-1] != value:
                record["observations"].append(value)
            return status

        try:
            initial = observe()
            resumed = resume_running_intent and (initial.get("desiredRunning") is True
                or initial.get("phase") in ("starting", "recovering", "running"))
            record["trigger"] = "stored_running_intent" if resumed else "ordinary_start"
            if not resumed:
                record["startCommandCount"] = 1
                try:
                    self.driver.invoke("desktop_start", timeout=self.remaining(deadline))
                except SmokeFailure as error:
                    if not expect_conflict or str(error) != "native_tun_device_conflict":
                        raise
            while True:
                status = observe()
                if status.get("statusStale") is not True and status.get("phase") in ("failed", "error"):
                    require(expect_conflict and status.get("errorCode") == "tun_device_conflict"
                            and status.get("operation") is None, "upgrade_start_failed_" + safe_code(status.get("errorCode")))
                    return status
                if status.get("statusStale") is not True and status.get("phase") == "running":
                    require(not expect_conflict and status.get("errorCode") != "tun_device_conflict",
                            "upgrade_tun_conflict_did_not_stop")
                    return status
                time.sleep(min(.1, self.remaining(deadline)))
        finally:
            record["elapsedMilliseconds"] = round((time.monotonic() - started) * 1000)
            self.checkpoint()

    def running(self, name: str, *, tun: bool = True) -> tuple[dict, object]:
        status = self.status()
        require(status.get("phase") == "running" and status.get("statusStale") is not True,
                "upgrade_tun_not_running")
        require(status.get("tun") is tun and status.get("systemProxy") is False
                and status.get("service", {}).get("running") is True, "upgrade_tun_not_service_owned")
        identity = self.request(status["controller"], "/desktop")
        require(identity.get("managed") is True and identity.get("state") == "running", "upgrade_core_not_managed")
        process = self.owner.processes.identity(identity.get("pid", 0))
        require(process and process.path == os.path.normcase(str(self.owner.core.resolve())), "upgrade_core_image_mismatch")
        self.retain_process(process)
        configuration = self.request(status["controller"], "/configs")
        require(configuration.get("tun", {}).get("enable") is tun
                and configuration["tun"].get("device") == name, "upgrade_actual_tun_name_mismatch")
        return status, process

    def retain_process(self, process) -> None:
        if process in self.child_handles:
            return
        api = self.owner.processes.api
        handle = api.OpenProcess(0x1000 | 0x100000, False, process.pid)
        require(handle, "upgrade_core_wait_handle_unavailable")
        if self.owner.processes.from_handle(handle, process.pid) != process:
            api.CloseHandle(handle)
            raise SmokeFailure("upgrade_core_wait_handle_identity_changed")
        self.child_handles[process] = handle
        self.children.append(process)

    def exited(self, process) -> bool:
        result = self.owner.processes.api.WaitForSingleObject(self.child_handles[process], 0)
        require(result in (0, 258), "upgrade_core_exit_wait_failed")
        return result == 0

    def capture(self, name: str, exclusive: bool, process) -> dict:
        identity = self.native.capture(name, exclusive=exclusive)
        for previous, creator in self.adapters:
            if previous["guid"] == identity["guid"]:
                require(creator == process or self.exited(creator),
                        "upgrade_adapter_has_two_live_creators")
        self.adapters = [(previous, creator) for previous, creator in self.adapters
                         if previous["guid"] != identity["guid"]]
        self.adapters.append((identity, process))
        return identity

    def forwarding(self, evidence: dict, label: str) -> None:
        before = self.socks.diagnostics()["forwarded"]
        try:
            wait_until(lambda: self.socks.roundtrip() and self.socks.diagnostics()["forwarded"] > before,
                       "upgrade_tun_forwarding_failed_" + label, 25)
        finally:
            evidence.setdefault("forwarding", {})[label] = {"forwardedBefore": before, **self.socks.diagnostics()}
            self.checkpoint()
        require(evidence["forwarding"][label]["forwarded"] > before, "upgrade_tun_forwarding_counter_did_not_advance")
        require(evidence["forwarding"][label]["unexpectedTargets"] == 0
                and evidence["forwarding"][label]["lastFixtureError"] is None,
                "upgrade_tun_forwarding_fixture_error")

    def assert_adapter_unchanged(self, before: dict, evidence: dict, stage: str, *,
                      phantom: bool | None, allow_removed: bool = False) -> str:
        actual = self.native.query(before["name"], before["guid"])
        evidence.setdefault("adapterObservations", {})[stage] = actual
        self.checkpoint()
        if actual is None:
            require(allow_removed, "upgrade_legacy_adapter_disappeared_before_recovery")
            return "removed_by_native_driver_after_create"
        require(same_identity(before, actual) and (phantom is None or actual["phantom"] is phantom),
                "upgrade_legacy_adapter_renamed_adopted_or_changed")
        if phantom is False:
            require(actual["interface"] == before["interface"], "upgrade_live_foreign_interface_changed")
        return "same_exact_identity"

    def assert_no_repair_controls(self, evidence: dict, *, offline: bool = False) -> None:
        controls = evidence.setdefault("removedRepairControls", {})
        anchors = evidence.setdefault("repairControlPageAnchors", {})
        self.driver.navigate("home")
        anchors["HomeScrollViewer"] = self.driver.ui("HomeScrollViewer").get("found")
        self.checkpoint()
        require(anchors["HomeScrollViewer"] is True, "upgrade_home_page_anchor_missing")
        controls["HomeUseManagedTunDevice"] = self.driver.ui("HomeUseManagedTunDevice").get("found")
        if offline:
            self.driver.navigate("proxies")
            anchors["RecoveryHint"] = self.driver.ui("RecoveryHint").get("found")
            self.checkpoint()
            require(anchors["RecoveryHint"] is True, "upgrade_offline_recovery_panel_missing")
            controls["RecoveryUseManagedTunDevice"] = self.driver.ui("RecoveryUseManagedTunDevice").get("found")
        self.driver.navigate("settings")
        for identifier in ("SettingsTunDeviceConfiguredName", "SettingsTunDeviceEffectiveName"):
            anchors[identifier] = self.driver.ui(identifier).get("found")
        self.checkpoint()
        require(anchors["SettingsTunDeviceConfiguredName"] is True
                and anchors["SettingsTunDeviceEffectiveName"] is True, "upgrade_settings_page_anchor_missing")
        controls["SettingsUseConfiguredTunDevice"] = self.driver.ui("SettingsUseConfiguredTunDevice").get("found")
        self.driver.navigate("home")
        self.checkpoint()
        require(all(value is False for value in controls.values()), "upgrade_manual_tun_repair_control_remains")

    def require_configured_name(self, status: dict, name: str) -> None:
        device = status.get("tunDevice", {})
        require(device.get("configuredName") == name and device.get("effectiveName") == name
                and device.get("managed") is not True
                and device.get("configRevision") == self.config_hash,
                "upgrade_unexpected_tun_name_override")
        preference = json.loads(self.preferences.read_text(encoding="utf-8"))
        require(preference.get("tunDeviceOverride") is None, "upgrade_unexpected_persisted_tun_override")

    def close_gui(self) -> None:
        if self.driver is None:
            return
        driver = self.driver
        if driver.process.poll() is None:
            driver.invoke("desktop_stop", timeout=25)
            driver.invoke("desktop_exit", {"force": True}, timeout=25)
            require(driver.process.wait(timeout=15) == 0, "upgrade_gui_exit_failed")
        driver.close()
        self.driver = None
        wait_until(lambda: not self.owner.processes.at_paths([self.owner.app, self.owner.host]),
                   "upgrade_gui_host_survived_exit", 15)

    def stop_child(self) -> None:
        if self.child is not None:
            if self.child.poll() is None:
                if self.child_managed:
                    with contextlib.suppress(Exception):
                        self.request(self.child_controller, "/desktop/shutdown", "POST")
                    try:
                        self.child.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        self.child.kill()  # Popen retains the exact CreateProcess handle.
                        self.child.wait(timeout=10)
                else:
                    # This private legacy foreign owner has no managed
                    # shutdown endpoint. End only its retained process handle;
                    # it is not the installed-app graceful-stop acceptance.
                    self.child.kill()
                    self.child.wait(timeout=10)
            if self.child_record is not None:
                self.child_record["exitCode"] = self.child.poll()
            self.child = None
        if self.child_output is not None:
            try:
                self.evidence.setdefault("isolatedCoreLogs", []).append({
                    "pid": self.child_identity.pid if self.child_identity else None,
                    "output": self.log_tail(self.child_output)})
            finally:
                self.child_output.close()
                self.child_output = None

    def finish_case(self) -> None:
        self.close_gui()
        self.stop_child()
        for identity in self.children:
            wait_until(lambda identity=identity: self.exited(identity),
                       "upgrade_core_survived_stop", 15)
        for adapter, creator in self.adapters:
            outcome = self.native.remove_fixture(adapter, self.child_handles[creator], self.owner.processes)
            self.evidence["cleanup"].append({"guid": adapter["guid"], "name": adapter["name"],
                "creatorPid": creator.pid, "creatorExitConfirmed": True, "outcome": outcome})
        self.adapters.clear()
        for handle in self.child_handles.values():
            self.owner.processes.api.CloseHandle(handle)
        self.child_handles.clear()
        self.children.clear()

    def verify_old_installation(self) -> None:
        self.owner.assert_registration(self.old_version)
        info = json.loads((self.owner.install_dir / "resources/build-info.json").read_text(encoding="utf-8-sig"))
        require(info.get("desktopVersion") == self.old_version and info.get("uiCommit") == self.old_source
                and info.get("coreCommit") == OLD_CORE_SOURCE and info.get("desktopFramework") == "WPF",
                "upgrade_old_runtime_source_mismatch")
        runtime = info.get("runtimeFiles", {})
        require(all(path.name in runtime for path in
                    (self.owner.app, self.owner.host, self.owner.helper, self.owner.core)), "upgrade_old_manifest_incomplete")
        for name, expected in runtime.items():
            path = Path(name)
            require(not path.is_absolute() and ".." not in path.parts and "\\" not in name
                    and file_hash(self.owner.install_dir / path) == expected, "upgrade_old_runtime_hash_mismatch")
        self.old_core = self.directory / "published-0.2.5-core.exe"
        shutil.copyfile(self.owner.core, self.old_core)
        require(file_hash(self.old_core) == runtime[self.owner.core.name], "upgrade_old_core_copy_mismatch")
        self.evidence["oldRuntimeFilesVerified"] = len(runtime)
        self.evidence["oldCoreSha256"] = file_hash(self.old_core)
        require(isinstance(info.get("coreVersion"), str)
                and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", info["coreVersion"]),
                "upgrade_old_core_version_invalid")
        self.old_core_version = "mihomo-optimized-v" + info["coreVersion"] + "-desktop"

    def hot_upgrade(self) -> None:
        from upgrade_smoke import uninstall_command

        self.owner.stage = "published_0_2_5_live_tun_upgrade"
        uninstaller = self.owner.install_dir / "uninstall.exe"
        require(self.owner.command("tun_before_old_uninstall", uninstaller, uninstall_command(uninstaller), 180)["exitCode"] == 0,
                "upgrade_tun_baseline_uninstall_failed")
        self.owner.assert_no_service()
        self.owner.assert_marker()
        uninstaller.unlink(missing_ok=True)
        require(self.owner.command("tun_old_install", self.baseline, ["/S"], 180)["exitCode"] == 0,
                "upgrade_tun_baseline_install_failed")
        self.verify_old_installation()
        name = "MihomoUpgrade-" + uuid.uuid4().hex[:12]
        self.native.require_unused(name)
        self.configure(self.source(name))
        before_service = self.owner.install_service("tun_old_service_install")
        owner_bytes = self.owner.owner_file.read_bytes()
        evidence = self.evidence["scenarios"]["hotUpgrade"] = {"configuredName": name,
            "sourceYamlSha256": self.config_hash}
        self.unchanged_source(evidence, "before_old_start")
        self.launch_gui("old-live")
        self.start_gui(evidence, "published_old")
        _, old_core = self.running(name)
        legacy = evidence["oldAdapter"] = self.capture(name, False, old_core)
        evidence["oldCorePid"] = old_core.pid
        self.forwarding(evidence, "before_upgrade")
        self.capture_logs("published_old_core_running")
        require(self.owner.command("tun_live_candidate_upgrade", self.owner.candidate, ["/S"], 180)["exitCode"] == 0,
                "upgrade_live_tun_installer_failed")
        require(self.driver.process.wait(timeout=15) == 0, "upgrade_old_gui_survived_installer")
        self.close_gui()
        require(self.exited(old_core), "upgrade_old_core_survived_installer")
        evidence["oldProcessExitConfirmed"] = True
        after_service = self.owner.running_service()
        require(after_service != before_service and self.owner.owner_file.read_bytes() == owner_bytes,
                "upgrade_tun_service_handoff_failed")
        self.owner.assert_candidate_files(configuration_hash=self.config_hash)
        self.unchanged_source(evidence, "after_installer")
        observed = self.native.query(name, legacy["guid"])
        require(observed is None or same_identity(legacy, observed), "upgrade_hot_legacy_identity_changed")
        evidence["legacyAfterInstaller"] = observed
        cursor = self.recovery_cursor()
        deadline = time.monotonic() + START_TIMEOUT
        self.launch_gui("candidate-hot")
        status = self.start_gui(evidence, "candidate", resume_running_intent=True, deadline=deadline)
        status, current_core = self.running(name)
        self.require_configured_name(status, name)
        evidence["candidateAdapter"] = self.capture(name, True, current_core)
        require(evidence["candidateAdapter"]["guid"] != legacy["guid"], "upgrade_hot_adopted_legacy_adapter")
        self.assert_recovery_events(cursor, [], evidence, "after_start")
        evidence["candidateTunDevice"] = status["tunDevice"]
        evidence["legacyAfterCreate"] = self.assert_adapter_unchanged(legacy, evidence, "after_automatic_start",
                                                          phantom=None, allow_removed=True)
        self.forwarding(evidence, "after_upgrade")
        self.unchanged_source(evidence, "after_upgrade_forwarding")
        self.assert_no_repair_controls(evidence)
        self.assert_recovery_events(cursor, [], evidence, "after_upgrade_forwarding")
        evidence.update({"sourceYamlSha256": self.config_hash, "servicePidBefore": before_service.pid,
                         "servicePidAfter": after_service.pid, "ownerSidPreserved": True})
        self.capture_logs("candidate_after_live_upgrade")
        self.owner.mark("published_0_2_5_live_tun_upgrade")

        # Roll back the actual installed application after its owner confirms
        # Stop. Neither direction changes the user's configured adapter name.
        self.close_gui()
        require(self.exited(current_core), "upgrade_candidate_core_survived_stop_before_rollback")
        require(self.owner.command("tun_same_name_rollback", self.baseline, ["/S"], 180)["exitCode"] == 0,
                "upgrade_same_name_rollback_install_failed")
        self.verify_old_installation()
        self.unchanged_source(evidence, "after_rollback_installer")
        self.launch_gui("same-name-rollback")
        self.start_gui(evidence, "rollback")
        _, rollback_core = self.running(name)
        evidence["rollbackAdapter"] = self.capture(name, False, rollback_core)
        require(evidence["rollbackAdapter"]["guid"] == legacy["guid"], "upgrade_rollback_changed_configured_identity")
        self.forwarding(evidence, "after_rollback")
        self.close_gui()
        require(self.exited(rollback_core), "upgrade_rollback_core_survived_stop")
        require(self.owner.command("tun_same_name_upgrade_again", self.owner.candidate, ["/S"], 180)["exitCode"] == 0,
                "upgrade_same_name_reinstall_failed")
        self.owner.assert_candidate_files(configuration_hash=self.config_hash)
        cursor = self.recovery_cursor()
        self.launch_gui("same-name-reupgrade")
        self.start_gui(evidence, "reupgrade")
        status, creator = self.running(name)
        self.require_configured_name(status, name)
        evidence["reupgradeAdapter"] = self.capture(name, True, creator)
        require(evidence["reupgradeAdapter"]["guid"] == evidence["candidateAdapter"]["guid"],
                "upgrade_reinstall_changed_product_identity")
        self.forwarding(evidence, "after_reupgrade")
        self.unchanged_source(evidence, "after_reupgrade")
        self.assert_recovery_events(cursor, [], evidence, "after_reupgrade")
        self.owner.mark("configured_tun_name_upgrade_and_rollback")
        self.finish_case()

    def isolated_core(self, name: str, *, candidate: bool, auto_route: bool) -> dict:
        require(self.child is None, "upgrade_isolated_owner_already_running")
        if candidate:
            core = self.directory / "candidate-core.exe"
            expected_hash = self.owner.info["runtimeFiles"][self.owner.core.name]
            require(file_hash(self.owner.core) == expected_hash, "upgrade_candidate_runtime_hash_changed")
            if not core.exists():
                shutil.copyfile(self.owner.core, core)
        else:
            core, expected_hash = self.old_core, self.evidence["oldCoreSha256"]
        require(file_hash(core) == expected_hash, "upgrade_isolated_core_copy_changed")
        self.native.require_unused(name)
        home = self.directory / (name + "-" + uuid.uuid4().hex[:8])
        home.mkdir()
        # A private owner is exercised through raw TUN traffic. It has no
        # mixed-port consumer, so do not probe/release another unused port.
        content = self.source(name, mixed=0).decode("utf-8").replace(
            "tun:\n  enable: false\n", "tun:\n  enable: true\n  auto-route: "
            + str(auto_route).lower() + "\n  auto-detect-interface: true\n")
        if not auto_route:
            # A live foreign owner should exercise only the name collision,
            # not duplicate IP addresses or a competing candidate /32 route.
            content = content.replace("198.18.0.1/16", "198.19.0.1/16").replace(
                "route-address: ['198.18.77.77/32']", "route-address: ['198.19.77.77/32']")
        (home / "config.yaml").write_text(content, encoding="utf-8")
        self.child_controller = {"url": "", "secret": secrets.token_urlsafe(32)}
        self.child_managed = candidate
        self.secrets.add(self.child_controller["secret"])
        environment = child_environment()
        environment["CLASH_OVERRIDE_SECRET"] = self.child_controller["secret"]
        nonce = secrets.token_hex(32) if candidate else None
        if candidate:
            environment["MIHOMO_DESKTOP_LAUNCH_NONCE"] = nonce
        self.child_identity = None
        self.child_record = None
        self.child_output = tempfile.TemporaryFile()
        arguments = [str(core), "-d", str(home), "-f", str(home / "config.yaml"),
                     "-ext-ctl", "127.0.0.1:0"]
        if candidate:
            arguments.append("-desktop-managed")
        self.child = subprocess.Popen(arguments,
            cwd=home, env=environment, stdin=subprocess.DEVNULL,
            stdout=self.child_output, stderr=self.child_output, close_fds=True,
            creationflags=subprocess.CREATE_NO_WINDOW)
        # Query the exact process returned by CreateProcess, not a fresh PID
        # lookup. Preserve its raw ProcessIdentity for the later kill guard;
        # only the comparison to the verified file needs path normalization.
        self.child_identity = self.owner.processes.from_handle(int(self.child._handle), self.child.pid)
        expected = os.path.normcase(str(core.resolve(strict=True)))
        self.child_record = {"pid": self.child.pid, "expectedPath": expected,
            "actualPath": self.child_identity.path if self.child_identity else None,
            "canonicalPath": None, "sameFile": False,
            "creationTime": str(self.child_identity.created) if self.child_identity else None,
            "exitCodeAtInspection": self.child.poll(), "exitCode": None, "candidate": candidate,
            "managed": candidate, "requestedController": "127.0.0.1:0", "mixedPort": 0,
            "verifiedSha256": expected_hash}
        self.evidence.setdefault("isolatedProcesses", []).append(self.child_record)
        if self.child_identity:
            try:
                self.child_record["canonicalPath"] = os.path.normcase(str(
                    Path(self.child_identity.path).resolve(strict=True)))
                # samefile checks the volume/file ID on Windows. Different
                # textual paths never authorize a different executable.
                self.child_record["sameFile"] = os.path.samefile(self.child_identity.path, core)
            except OSError as error:
                self.child_record["pathQueryError"] = {"type": type(error).__name__,
                    "winerror": getattr(error, "winerror", None)}
        self.checkpoint()
        require(self.child_identity and self.child_record["canonicalPath"] == expected
                and self.child_record["sameFile"] is True,
                "upgrade_isolated_core_identity_mismatch")
        self.retain_process(self.child_identity)

        def ready():
            require(self.child.poll() is None, "upgrade_isolated_core_exited")
            record = read_controller_record(home, self.child.pid, nonce) if candidate else None
            if candidate and record is None:
                return None
            listeners = self.native.loopback_listeners(
                self.child_identity, self.child_handles[self.child_identity], self.owner.processes)
            if not listeners and not candidate:
                return None
            require(len(listeners) == 1, "upgrade_isolated_controller_listener_ambiguous")
            listener = listeners[0]
            address = listener["address"] + ":" + str(listener["port"])
            if candidate:
                require(record["controller"] == address, "upgrade_controller_record_listener_mismatch")
            self.child_controller["url"] = "http://" + address
            self.child_record["controllerDiscovery"] = {
                "mode": "managed_record" if candidate else "legacy_native_owner_table",
                "pid": listener["pid"], "port": listener["port"],
                "coreInstance": record["coreInstance"] if candidate else None,
                "launchNonceMatched": candidate, "exactProcessHandleVerified": True}
            try:
                value = self.request(self.child_controller, "/desktop" if candidate else "/version")
            except (OSError, http.client.HTTPException):
                return None
            require(not self.exited(self.child_identity)
                    and self.owner.processes.from_handle(int(self.child._handle), self.child.pid) == self.child_identity,
                    "upgrade_isolated_core_identity_changed")
            if candidate:
                require(value.get("pid") == self.child.pid and value.get("managed") is True
                        and value.get("api_version") == 1 and value.get("core_instance") == record["coreInstance"],
                        "upgrade_isolated_core_controller_mismatch")
                return value if value.get("state") == "running" else None
            require(value.get("version") == self.old_core_version, "upgrade_legacy_controller_version_mismatch")
            self.child_record["controllerDiscovery"]["verifiedVersion"] = value["version"]
            return value

        wait_until(ready, "upgrade_isolated_core_start_timeout", 45)
        configuration = self.request(self.child_controller, "/configs")
        require(configuration.get("mixed-port") == 0 and configuration.get("port") == 0
                and configuration.get("socks-port") == 0, "upgrade_isolated_unused_proxy_listener_enabled")
        require(configuration.get("tun", {}).get("enable") is True
                and configuration["tun"].get("device") == name, "upgrade_isolated_tun_not_enabled")
        if not auto_route:
            tun = configuration["tun"]
            require(tun.get("auto-route") is False and tun.get("inet4-address") == ["198.19.0.1/30"]
                    and tun.get("route-address") == ["198.19.77.77/32"], "upgrade_foreign_tun_addresses_not_isolated")
        return self.capture(name, candidate, self.child_identity)

    def require_tun_failure(self, status: dict, code: str) -> dict:
        failure = status.get("lastOperationFailure")
        require(isinstance(failure, dict) and failure.get("operation") == "tun_change"
                and failure.get("code") == code and isinstance(failure.get("message"), str)
                and failure["message"] and failure.get("requestedTun") is True
                and failure.get("appliedTun") is False
                and type(failure.get("occurredAt")) is int and 0 < failure["occurredAt"] <= time.time() * 1000 + 1000
                and type(failure.get("intentRevision")) is int
                and failure["intentRevision"] <= status.get("intentRevision", -1),
                "upgrade_tun_operation_failure_contract_invalid")
        require(status.get("phase") == "running" and status.get("processState") == "alive"
                and status.get("controlState") == "ready" and status.get("tun") is False
                and status.get("tunHealth") == "disabled" and not status.get("lastError")
                and not status.get("errorCode") and status.get("statusStale") is not True,
                "upgrade_historical_tun_failure_overrode_current_proxy_health")
        return failure

    def proxy_forwarding(self, status: dict, evidence: dict, label: str) -> None:
        configuration = self.request(status["controller"], "/configs")
        port = configuration.get("mixed-port")
        require(type(port) is int and 0 < port < 65536 and configuration.get("tun", {}).get("enable") is False,
                "upgrade_proxy_forwarding_requires_confirmed_tun_disabled")
        before = self.socks.diagnostics()["forwarded"]
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
        try:
            connection.request("GET", "http://" + TUN_TARGET + "/mihomo-tun-smoke",
                               headers={"Host": TUN_TARGET, "Connection": "close"})
            response = connection.getresponse()
            require(response.status == 200 and response.read(4096) == TUN_BODY,
                    "upgrade_ordinary_proxy_not_forwarding_after_rejected_tun")
        finally:
            connection.close()
        wait_until(lambda: self.socks.diagnostics()["forwarded"] > before,
                   "upgrade_proxy_forwarding_counter_did_not_advance", 3)
        evidence.setdefault("proxyForwarding", {})[label] = {"forwardedBefore": before, **self.socks.diagnostics()}
        require(self.socks.diagnostics()["unexpectedTargets"] == 0
                and self.socks.diagnostics()["lastFixtureError"] is None, "upgrade_proxy_forwarding_fixture_error")
        self.checkpoint()

    def failure_ui(self, evidence: dict, failure: dict | None) -> None:
        expected = self.status()
        require(type(expected.get("intentRevision")) is int and type(expected.get("pid")) is int
                and type(expected.get("statusSequence")) is int and type(expected.get("tun")) is bool,
                "upgrade_failure_ui_status_identity_invalid")
        health = "healthy" if expected["tun"] else "disabled"
        self.driver.navigate("home")

        def ready_sidebar():
            current = self.status(timeout=3)
            require(current.get("intentRevision") == expected["intentRevision"]
                    and current.get("pid") == expected["pid"] and current.get("tun") is expected["tun"]
                    and current.get("statusSequence", -1) >= expected["statusSequence"],
                    "upgrade_failure_ui_runtime_identity_changed")
            require(current.get("lastOperationFailure") == failure,
                    "upgrade_failure_ui_operation_history_changed")
            if not (current.get("phase") == "running" and current.get("processState") == "alive"
                    and current.get("controlState") == "ready" and current.get("tunHealth") == health
                    and current.get("statusStale") is not True and current.get("operation") is None
                    and current.get("lastError") is None and current.get("errorCode") is None):
                return None
            phase = self.driver.ui("CorePhase")
            return phase if phase.get("found") is True and phase.get("name") in ("运行中", "Running") else None

        # Successful TUN application may still be checking its first health
        # sample. Wait for this intent's live state and its actual UI label.
        wait_until(ready_sidebar, "upgrade_live_sidebar_still_reports_historical_tun_failure", 15)
        self.driver.navigate("settings")
        for page, identifier in (("settings", "settingsLastOperationFailure"), ("home", "homeLastOperationFailure")):
            self.driver.navigate(page)
            notice = self.driver.ui(identifier)
            evidence.setdefault("operationFailureUi", {}).setdefault(page, []).append(notice)
            if failure is None:
                require(not notice.get("found") or not notice.get("name"), "upgrade_cleared_failure_still_visible")
            else:
                stamp = datetime.fromtimestamp(failure["occurredAt"] / 1000).strftime("%Y-%m-%d %H:%M:%S")
                require(notice.get("found") is True and stamp in notice.get("name", "")
                        and failure["message"] in notice["name"]
                        and ("当前 TUN 已关闭" in notice["name"] or "TUN is currently off" in notice["name"]),
                        "upgrade_failed_tun_request_or_timestamp_hidden")
        self.checkpoint()

    def owned_active_collision(self) -> None:
        self.owner.stage = "same_product_tun_active_owner"
        name = "MihomoUpgrade-" + uuid.uuid4().hex[:12]
        self.configure(self.source(name), tun=False)
        evidence = self.evidence["scenarios"]["sameProductActiveOwner"] = {
            "configuredName": name, "sourceYamlSha256": self.config_hash}
        cursor = self.recovery_cursor()
        self.launch_gui("owned-active")
        self.start_gui(evidence, "ordinary_proxy")
        _, proxy_core = self.running(name, tun=False)
        owner = evidence["activeOwnerAdapter"] = self.isolated_core(name, candidate=True, auto_route=True)
        evidence["activeOwnerPid"] = self.child_identity.pid
        self.forwarding(evidence, "active_owner_before_collision")
        self.driver.invoke("desktop_set_tun", {"enabled": True}, expected_error="tun_device_busy", timeout=45)
        status, proxy_after = self.running(name, tun=False)
        failure = self.require_tun_failure(status, "tun_device_busy")
        require(proxy_after == proxy_core and self.child.poll() is None, "upgrade_rejected_tun_replaced_an_active_owner")
        self.require_configured_name(status, name)
        self.assert_adapter_unchanged(owner, evidence, "after_rejected_enable", phantom=False)
        self.forwarding(evidence, "active_owner_after_collision")
        self.proxy_forwarding(status, evidence, "after_rejected_enable")
        self.unchanged_source(evidence, "after_rejected_enable")
        self.owner.mark("same_product_tun_active_owner_preserved")

        # Wait for an actual newer health observation, not a fixed-duration
        # sleep or a new status envelope that may still contain cached health.
        observed_at = status.get("observedAt", 0)
        def next_health():
            current = self.status()
            require(self.require_tun_failure(current, "tun_device_busy") == failure,
                    "upgrade_health_poll_acknowledged_failed_enable")
            return current if current.get("observedAt", 0) > observed_at else None
        wait_until(next_health, "upgrade_health_observation_did_not_advance", 15)
        self.driver.invoke("desktop_patch_config", {"patch": {"log-level": "debug"}}, timeout=30)
        self.config_hash = file_hash(self.owner.config)  # Explicit fixture scalar edit, not an automatic name rewrite.
        status, unchanged_core = self.running(name, tun=False)
        require(unchanged_core == proxy_core and self.require_tun_failure(status, "tun_device_busy") == failure,
                "upgrade_scalar_change_cleared_failed_tun_intent")
        self.require_configured_name(status, name)
        evidence["sourceSha256AfterExplicitScalarEdit"] = self.config_hash
        self.driver.invoke("desktop_reload_config", {"force": True}, timeout=95)
        status, unchanged_core = self.running(name, tun=False)
        require(unchanged_core == proxy_core and self.require_tun_failure(status, "tun_device_busy") == failure,
                "upgrade_unchanged_reload_acknowledged_failed_tun_enable")
        self.proxy_forwarding(status, evidence, "after_scalar_and_unchanged_reload")
        self.unchanged_source(evidence, "after_unchanged_reload")
        self.failure_ui(evidence, failure)

        # Explicitly choosing Off acknowledges the failed request even though
        # the confirmed applied value is already false. A fresh failure has a
        # later host intent; an unrelated poll must not resurrect the first one.
        self.driver.invoke("desktop_set_tun", {"enabled": False}, timeout=30)
        require(self.status().get("lastOperationFailure") is None, "upgrade_manual_off_did_not_acknowledge_failure")
        self.failure_ui(evidence, None)
        self.driver.invoke("desktop_set_tun", {"enabled": True}, expected_error="tun_device_busy", timeout=45)
        latest = self.require_tun_failure(self.status(), "tun_device_busy")
        require(latest["intentRevision"] > failure["intentRevision"] and latest["occurredAt"] >= failure["occurredAt"],
                "upgrade_repeated_failure_reused_stale_operation_identity")
        evidence["failedOperations"] = [failure, latest]
        self.assert_adapter_unchanged(owner, evidence, "after_repeated_enable", phantom=False)
        self.forwarding(evidence, "active_owner_after_repeated_collision")

        # Synchronize on the exact owner's process handle. The very next
        # operation is Create through the ordinary TUN switch: no alias wait,
        # PnP manipulation, native inspection or retry manufactures success.
        self.request(self.child_controller, "/desktop/shutdown", "POST")
        require(self.child.wait(timeout=10) == 0 and self.exited(self.child_identity),
                "upgrade_owned_graceful_exit_unconfirmed")
        started = time.monotonic()
        self.driver.invoke("desktop_set_tun", {"enabled": True}, timeout=45)
        evidence["closeToCreateMilliseconds"] = round((time.monotonic() - started) * 1000)
        self.stop_child()
        status, current = self.running(name)
        require(current == proxy_core and status.get("lastOperationFailure") is None,
                "upgrade_successful_tun_retry_replaced_core_or_retained_failure")
        adapter = evidence["adapterAfterClose"] = self.capture(name, True, current)
        require(adapter["guid"] == owner["guid"], "upgrade_close_recreate_changed_product_identity")
        self.forwarding(evidence, "after_immediate_recreate")
        self.owner.mark("same_product_tun_close_immediate_recreate")

        evidence["rapidToggles"] = []
        for cycle in range(3):
            started = time.monotonic()
            self.driver.invoke("desktop_set_tun", {"enabled": False}, timeout=30)
            self.driver.invoke("desktop_set_tun", {"enabled": True}, timeout=45)
            elapsed = round((time.monotonic() - started) * 1000)
            status, current = self.running(name)
            current_adapter = self.capture(name, True, current)
            require(current == proxy_core and current_adapter["guid"] == owner["guid"]
                    and status.get("lastOperationFailure") is None,
                    "upgrade_rapid_tun_toggle_replaced_owner_identity_or_restored_old_error")
            self.require_configured_name(status, name)
            self.forwarding(evidence, "rapid_toggle_" + str(cycle))
            evidence["rapidToggles"].append({"cycle": cycle, "milliseconds": elapsed, "pid": current.pid,
                                             "guid": current_adapter["guid"], "interface": current_adapter["interface"]})
        self.failure_ui(evidence, None)
        reload_controller = status["controller"]

        def reload_health(not_before: float) -> dict | None:
            observed = self.request(reload_controller, "/desktop/health")
            require(observed.get("managed") is True and observed.get("state") == "running"
                    and observed.get("pid") == proxy_core.pid
                    and isinstance(observed.get("core_instance"), str) and observed["core_instance"]
                    and all(type(observed.get(key)) is int and observed[key] > 0
                            for key in ("config_generation", "tun_generation", "creation_count")),
                    "upgrade_reload_health_identity_invalid")
            tun = observed.get("tun") or {}
            if observed.get("stale") is not False or tun.get("state") != "healthy":
                return None
            require(tun.get("enabled") is True and tun.get("device") == name
                    and isinstance(tun.get("identity"), str) and tun["identity"]
                    and isinstance(observed.get("observed_at"), str), "upgrade_reload_tun_health_invalid")
            try:
                sampled = datetime.fromisoformat(observed["observed_at"].replace("Z", "+00:00"))
            except ValueError:
                raise SmokeFailure("upgrade_reload_health_timestamp_invalid") from None
            require(sampled.tzinfo is not None, "upgrade_reload_health_timestamp_invalid")
            return observed if sampled.timestamp() >= not_before else None

        # The source deliberately omits GSO. Applied listener defaults must
        # survive the next full reload without replacing this device again.
        require(b"gso-max-size" not in self.owner.config.read_bytes(), "upgrade_reload_fixture_has_explicit_gso")
        require(self.request(reload_controller, "/configs")["tun"].get("gso-max-size") == 65536,
                "upgrade_reload_applied_gso_default_missing")
        before_reload = wait_until(lambda: reload_health(0), "upgrade_reload_initial_health_not_fresh", 15)
        before_adapter = self.capture(name, True, current)
        facts = ("pid", "core_instance", "config_generation", "tun_generation", "creation_count", "observed_at")
        reload_evidence = evidence["reloadPreservedTun"] = {
            "before": {key: before_reload[key] for key in facts}, "beforeAdapter": before_adapter}
        self.checkpoint()
        started = time.monotonic()
        self.driver.invoke("desktop_reload_config", {"force": True}, timeout=95)
        reload_evidence["milliseconds"] = round((time.monotonic() - started) * 1000)
        reload_completed_at = time.time()
        status, current = self.running(name)
        require(current == proxy_core and status.get("lastOperationFailure") is None,
                "upgrade_reload_replaced_owner_or_restored_old_error")
        after_reload = wait_until(lambda: reload_health(reload_completed_at),
                                  "upgrade_reload_completed_health_not_fresh", 15)
        after_adapter = self.capture(name, True, current)
        reload_evidence.update({"after": {key: after_reload[key] for key in facts}, "afterAdapter": after_adapter})
        self.checkpoint()
        require(all(after_reload[key] == before_reload[key]
                    for key in ("pid", "core_instance", "tun_generation", "creation_count"))
                and after_reload["config_generation"] >= before_reload["config_generation"]
                and after_reload["tun"]["identity"] == before_reload["tun"]["identity"]
                and same_identity(before_adapter, after_adapter)
                and after_adapter["interface"] == before_adapter["interface"],
                "upgrade_same_source_reload_recreated_tun")
        require(self.request(reload_controller, "/configs")["tun"].get("gso-max-size") == 65536,
                "upgrade_reload_changed_applied_gso_default")
        self.require_configured_name(status, name)
        self.forwarding(evidence, "after_config_reload")
        self.unchanged_source(evidence, "after_rapid_toggle_and_reload")
        self.assert_recovery_events(cursor, [], evidence, "after_rapid_toggle_and_reload")
        self.owner.mark("tun_failure_lifecycle_and_fast_toggle")
        self.capture_logs("same-product-active-owner")
        self.finish_case()

    def owned_forced_exit(self) -> None:
        self.owner.stage = "same_product_tun_forced_exit"
        name = "MihomoUpgrade-" + uuid.uuid4().hex[:12]
        self.configure(self.source(name), tun=False)
        evidence = self.evidence["scenarios"]["sameProductForcedExit"] = {
            "configuredName": name, "sourceYamlSha256": self.config_hash}
        cursor = self.recovery_cursor()
        self.launch_gui("owned-forced-exit")
        self.start_gui(evidence, "ordinary_proxy")
        _, proxy_core = self.running(name, tun=False)
        owner = evidence["previousAdapter"] = self.isolated_core(name, candidate=True, auto_route=True)
        evidence["previousOwnerPid"] = self.child_identity.pid
        self.forwarding(evidence, "before_forced_exit")
        self.owner.processes.kill(self.child_identity)
        require(self.child.wait(timeout=10) != 0 and self.exited(self.child_identity),
                "upgrade_same_product_forced_exit_unconfirmed")
        started = time.monotonic()
        self.driver.invoke("desktop_set_tun", {"enabled": True}, timeout=45)
        evidence["exitToCreateMilliseconds"] = round((time.monotonic() - started) * 1000)
        self.stop_child()
        status, current = self.running(name)
        require(current == proxy_core and status.get("lastOperationFailure") is None,
                "upgrade_forced_exit_recovery_replaced_proxy_core_or_failed")
        adapter = evidence["currentAdapter"] = self.capture(name, True, current)
        require(adapter["guid"] == owner["guid"], "upgrade_forced_exit_recovery_changed_product_identity")
        self.require_configured_name(status, name)
        self.forwarding(evidence, "after_forced_exit_recreate")
        self.unchanged_source(evidence, "after_forced_exit_recreate")
        self.assert_recovery_events(cursor, [], evidence, "after_forced_exit_recreate")
        self.owner.mark("same_product_tun_forced_exit_recreate")
        self.capture_logs("same-product-forced-exit")
        self.finish_case()

    def restarted_health(self, previous, intent_revision: int, deadline: float, record: dict) -> dict | None:
        status = self.status(timeout=min(5, self.remaining(deadline)))
        record["lastStatus"] = dict(self.evidence["lastStatus"])
        if status.get("statusStale") is True or status.get("intentRevision", 0) <= intent_revision:
            return None
        require(status.get("phase") not in ("failed", "error")
                and status.get("errorCode") is None and status.get("lastOperationFailure") is None,
                "upgrade_desktop_restart_failed_" + safe_code(status.get("errorCode")))
        if status.get("phase") != "running" or status.get("operation") is not None:
            return None
        require(type(status.get("pid")) is int and status["pid"] != previous.pid
                and status.get("desiredRunning") is True and status.get("processState") == "alive"
                and status.get("controlState") == "ready" and status.get("listenersReady") is True,
                "upgrade_desktop_restart_did_not_replace_core")
        health = self.request(status["controller"], "/desktop/health")
        require(health.get("managed") is True and health.get("state") == "running"
                and health.get("pid") == status["pid"] and isinstance(health.get("core_instance"), str)
                and health["core_instance"]
                and all(type(health.get(key)) is int and health[key] > 0
                        for key in ("config_generation", "tun_generation", "creation_count")),
                "upgrade_desktop_restart_health_identity_invalid")
        tun = health.get("tun") or {}
        record["lastHealth"] = {key: health.get(key) for key in
            ("pid", "core_instance", "config_generation", "tun_generation", "creation_count", "stale", "observed_at")}
        record["lastHealth"]["tun"] = {key: tun.get(key) for key in ("enabled", "state", "device", "identity", "index")}
        if health.get("stale") is not False or tun.get("state") != "healthy":
            return None
        require(tun.get("enabled") is True and tun.get("device") == "utun"
                and isinstance(tun.get("identity"), str) and re.fullmatch(r"[0-9a-f]{16}", tun["identity"]),
                "upgrade_desktop_restart_tun_identity_invalid")
        return health

    def confirm_process_handoff(self, previous, current) -> dict:
        require(previous.pid != current.pid and self.exited(previous) and not self.exited(current),
                "upgrade_desktop_restart_previous_process_alive")
        times = [wintypes.FILETIME() for _ in range(4)]
        require(self.owner.processes.api.GetProcessTimes(self.child_handles[previous],
                    *(ctypes.byref(value) for value in times)), "upgrade_desktop_restart_process_times_unavailable")
        created, exited = ((value.dwHighDateTime << 32) | value.dwLowDateTime for value in times[:2])
        require(created == previous.created and created <= exited <= current.created,
                "upgrade_desktop_restart_spawned_before_previous_exit")
        return {"previousPid": previous.pid, "currentPid": current.pid, "previousExitConfirmed": True,
                "previousExitFileTime": str(exited), "currentCreationFileTime": str(current.created)}

    def desktop_restarts(self) -> None:
        self.owner.stage = "desktop_tun_repeated_restart"
        name = "utun"
        self.native.require_unused(name)
        self.configure(self.source(name))
        evidence = self.evidence["scenarios"]["desktopRestarts"] = {
            "configuredName": name, "sourceYamlSha256": self.config_hash,
            "requiredCycles": DESKTOP_RESTART_CYCLES, "cycles": []}
        self.launch_gui("desktop-restarts")
        self.start_gui(evidence, "initial")
        status, previous = self.running(name)
        original_adapter = evidence["initialAdapter"] = self.capture(name, True, previous)
        self.forwarding(evidence, "before_restart")
        self.driver.navigate("home")
        wait_until(lambda: self.driver.ui("RestartCore").get("enabled") is True,
                   "upgrade_desktop_restart_button_not_ready", 15)
        last_instance = self.request(status["controller"], "/desktop")["core_instance"]

        for cycle in range(DESKTOP_RESTART_CYCLES):
            record = {"cycle": cycle, "trigger": "RestartCore" if cycle == 0 else "desktop_restart",
                      "previousPid": previous.pid, "restartCommandCount": 1, "passed": False}
            evidence["cycles"].append(record)
            self.checkpoint()
            started = time.monotonic()
            deadline = started + START_TIMEOUT
            try:
                # One real UI action covers the button; the remaining cycles
                # use that same public host command without UIA launch overhead.
                # Stop and Start remain inside the product. Nothing here waits
                # for PnP disappearance or retries a failed restart command.
                if cycle == 0:
                    require(self.driver.ui("RestartCore", "invoke").get("invoked") is True,
                            "upgrade_desktop_restart_button_not_invoked")
                else:
                    self.driver.invoke("desktop_restart", timeout=self.remaining(deadline))
                record["commandMilliseconds"] = round((time.monotonic() - started) * 1000)
                health = wait_until(lambda: self.restarted_health(previous, status["intentRevision"], deadline, record),
                                    "upgrade_desktop_restart_not_ready", self.remaining(deadline))
                status, current = self.running(name)
                record.update(self.confirm_process_handoff(previous, current))
                require(health["pid"] == current.pid and health["core_instance"] != last_instance,
                        "upgrade_desktop_restart_reused_core_instance")
                adapter = record["adapter"] = self.capture(name, True, current)
                require(adapter["guid"] == original_adapter["guid"]
                        and int(adapter["interface"]["luid"]) == int(health["tun"]["identity"], 16)
                        and adapter["interface"]["index"] == health["tun"]["index"],
                        "upgrade_desktop_restart_changed_name_or_native_identity")
                require(self.owner.processes.at_paths([self.owner.core]) == [current],
                        "upgrade_desktop_restart_has_multiple_cores")
                self.require_configured_name(status, name)
                self.forwarding(record, "after_restart")
                self.unchanged_source(record, "after_restart")
                previous, last_instance = current, health["core_instance"]
                record["passed"] = True
            except Exception as error:
                record["failure"] = safe_code(str(error)) if isinstance(error, SmokeFailure) else type(error).__name__
                try:
                    self.status(timeout=5)
                    record["failureStatus"] = dict(self.evidence["lastStatus"])
                except Exception as observation_error:
                    record["failureStatusError"] = type(observation_error).__name__
                try:
                    record["failureInterfaces"] = self.native.interfaces(name)
                    record["failureDevice"] = self.native.query(name, expected_guid(name, exclusive=True))
                except Exception as observation_error:
                    record["failureNativeError"] = (safe_code(str(observation_error))
                        if isinstance(observation_error, SmokeFailure) else type(observation_error).__name__)
                self.capture_logs("desktop-restart-failure-" + str(cycle))
                raise
            finally:
                record["milliseconds"] = round((time.monotonic() - started) * 1000)
                self.checkpoint()

        require(len(evidence["cycles"]) == DESKTOP_RESTART_CYCLES
                and all(record["passed"] for record in evidence["cycles"]), "upgrade_desktop_restart_cycles_incomplete")
        self.capture_logs("desktop-restarts-complete")
        self.owner.mark("desktop_tun_repeated_restart")
        self.finish_case()

    def foreign_name_collision(self) -> None:
        self.owner.stage = "live_foreign_tun_preserved"
        name = "MihomoUpgrade-" + uuid.uuid4().hex[:12]
        self.configure(self.source(name))
        evidence = self.evidence["scenarios"]["liveForeign"] = {
            "configuredName": name, "sourceYamlSha256": self.config_hash}
        legacy = evidence["foreignAdapter"] = self.isolated_core(name, candidate=False, auto_route=True)
        evidence["foreignCorePid"] = self.child_identity.pid
        before = [row for row in self.native.interfaces() if row["name"].casefold().startswith(name.casefold())]
        require(before == [legacy["interface"]]
                and self.native.query(name, expected_guid(name, exclusive=True)) is None,
                "upgrade_foreign_name_fixture_not_isolated")
        self.forwarding(evidence, "foreign_owner_before_collision")
        cursor = self.recovery_cursor()
        self.launch_gui("foreign-name-collision")
        status = self.start_gui(evidence, "rejected_start", expect_conflict=True)
        self.require_configured_name(status, name)
        self.assert_adapter_unchanged(legacy, evidence, "after_rejected_start", phantom=False)
        self.forwarding(evidence, "foreign_owner_after_collision")
        for _ in range(3):
            status = self.status()
            require(status.get("phase") in ("failed", "error") and status.get("errorCode") == "tun_device_conflict"
                    and status.get("pid") is None and status.get("controller") is None
                    and status.get("operation") is None and self.child.poll() is None,
                    "upgrade_foreign_collision_not_terminal_or_owner_exited")
            require(not self.owner.processes.at_paths([self.owner.core]), "upgrade_foreign_collision_core_respawned")
        after = [row for row in self.native.interfaces() if row["name"].casefold().startswith(name.casefold())]
        require(after == before and self.native.query(name, expected_guid(name, exclusive=True)) is None,
                "upgrade_foreign_collision_created_suffix_or_product_adapter")
        evidence.update({"interfacesBefore": before, "interfacesAfter": after})
        self.unchanged_source(evidence, "after_rejected_start")
        self.assert_recovery_events(cursor, [], evidence, "after_rejected_start")
        self.assert_no_repair_controls(evidence, offline=True)
        self.driver.invoke("desktop_stop", timeout=25)
        require(self.status().get("lastOperationFailure") is None, "upgrade_stop_retained_old_operation_failure")
        self.assert_adapter_unchanged(legacy, evidence, "after_candidate_stop", phantom=False)
        self.forwarding(evidence, "foreign_owner_after_candidate_stop")
        self.capture_logs("foreign-name-collision")
        self.owner.mark("live_foreign_tun_preserved")
        self.finish_case()

    def run(self) -> None:
        try:
            core_pin = json.loads(Path(__file__).resolve().parents[1].joinpath("core.json").read_text(encoding="utf-8"))
            require(self.owner.info.get("coreCommit") == core_pin.get("commit")
                    and self.owner.info.get("repository") == core_pin.get("repository"),
                    "upgrade_candidate_core_pin_changed")
            self.desktop = RestrictedDesktop()
            require(self.desktop.sid == self.owner.sid, "upgrade_gui_owner_sid_changed")
            self.socks = SocksFixture()
            self.hot_upgrade()
            self.owned_active_collision()
            self.owned_forced_exit()
            self.desktop_restarts()
            self.foreign_name_collision()
            require(self.owner.command("tun_upgrade_service_remove", self.owner.helper, ["--uninstall"])["exitCode"] == 0,
                    "upgrade_tun_service_remove_failed")
            self.owner.assert_no_service()
        finally:
            self.cleanup()

    def cleanup(self) -> None:
        def attempt(label, action):
            try:
                action()
            except Exception as error:
                self.owner.report["diagnostics"].append({"stage": "upgrade_tun_cleanup", "code": label,
                    "detail": safe_code(str(error)) if isinstance(error, SmokeFailure) else type(error).__name__})

        attempt("fixture_log_capture", lambda: self.capture_logs("final_" + self.owner.stage))
        attempt("gui_stop", self.close_gui)
        if self.driver is not None:
            attempt("gui_exact_handle_close", self.driver.close)
            self.driver = None
        attempt("isolated_child_stop", self.stop_child)
        attempt("owned_service_stop", self.owner.remove_owned_service)
        for identity in self.children:
            attempt("owned_core_stop", lambda identity=identity: self.owner.processes.kill(identity))
        for adapter, creator in self.adapters:
            attempt("captured_adapter_remove", lambda adapter=adapter, creator=creator:
                    self.native.remove_fixture(adapter, self.child_handles[creator], self.owner.processes))
        for handle in self.child_handles.values():
            attempt("creator_wait_handle_close", lambda handle=handle: self.owner.processes.api.CloseHandle(handle))
        self.child_handles.clear()
        if self.socks is not None:
            attempt("socks_fixture_close", self.socks.close)
        if self.desktop is not None:
            attempt("filtered_token_close", self.desktop.close)
        attempt("temporary_core_copy_remove", self.temp.cleanup)
        self.owner.config.write_bytes(self.original_config)
        if self.original_preferences is None:
            self.preferences.unlink(missing_ok=True)
        else:
            self.preferences.write_bytes(self.original_preferences)
        self.owner.assert_marker()
        self.evidence["originalFixtureConfigurationRestored"] = True
        self.checkpoint()
