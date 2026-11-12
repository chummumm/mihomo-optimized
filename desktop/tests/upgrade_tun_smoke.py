"""Actual 0.2.5 TUN upgrade and automatic desktop-name recovery acceptance.

The hot upgrade uses the installed published GUI, host, service and core. The
crash/foreign cases use a verified copy of that same old core as an isolated
child, so an old supervisor cannot restart Wintun and erase the evidence before
the candidate sees it. Each startup makes at most one ordinary Start request;
the fixture never invokes a repair command or a repair button.
"""

from __future__ import annotations

import contextlib
import hashlib
import http.client
import ipaddress
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import time
from urllib.parse import urlsplit
import uuid

from upgrade_tun_native import MANAGED_NAME, NativeTunIdentity, expected_guid, same_identity
from windows_smoke import (
    NativeDriver, RestrictedDesktop, SmokeFailure, SocksFixture, child_environment,
    free_port, profile, require, safe_code, wait_until,
)


OLD_CORE_SOURCE = "96c428bce6b1e7d68327910dad5f426ad29d2d98"
CURRENT_CORE_SOURCE = "182188b78acc600ff13c82b8d2d3f5c2d70d02d2"
START_TIMEOUT = 60.0
AUTO_RECOVERY_EVENTS = ("tun_device_auto_recovery", "tun_device_auto_recovery_succeeded")


def file_hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


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
                         "oldCoreSource": OLD_CORE_SOURCE, "candidateCoreSource": CURRENT_CORE_SOURCE,
                         "scenarios": {}, "cleanup": []}
        owner.report["nativeTunUpgrade"] = self.evidence

    def checkpoint(self) -> None:
        self.owner.write_report()

    def source(self, name: str) -> bytes:
        return ("# real-upgrade-tun-" + uuid.uuid4().hex + "\n"
                + profile(free_port(), self.socks.server_address[1]).replace(
                    "  device: MihomoSmokeTun\n", "  device: " + name + "\n")).encode("utf-8")

    def configure(self, content: bytes) -> None:
        require(self.driver is None, "upgrade_configure_requires_stopped_gui")
        self.owner.config.write_bytes(content)
        self.preferences.write_text(json.dumps({"desiredRunning": False, "systemProxy": False, "tun": True}),
                                    encoding="utf-8")
        self.config_hash = file_hash(self.owner.config)

    def unchanged_source(self, evidence: dict, stage: str) -> None:
        actual = file_hash(self.owner.config)
        evidence.setdefault("sourceSha256ByStage", {})[stage] = actual
        self.checkpoint()
        require(actual == self.config_hash, "upgrade_tun_source_yaml_changed")

    @staticmethod
    def request(controller: dict, path: str, method: str = "GET") -> dict:
        url = urlsplit(controller.get("url", ""))
        secret = controller.get("secret", "")
        require(url.scheme == "http" and url.hostname == "127.0.0.1" and url.port
                and not url.username and not url.password and len(secret) >= 32,
                "upgrade_controller_identity_invalid")
        connection = http.client.HTTPConnection("127.0.0.1", url.port, timeout=3)
        try:
            connection.request(method, path, headers={"Authorization": "Bearer " + secret, "Connection": "close"})
            response = connection.getresponse()
            content = response.read(1024 * 1024 + 1)
            require(response.status == 200 and len(content) <= 1024 * 1024
                    and secret.encode() not in content, "upgrade_controller_response_invalid")
            return json.loads(content) if content else {}
        finally:
            connection.close()

    def status(self, timeout: float = 5) -> dict:
        status = self.driver.invoke("desktop_status", timeout=timeout)
        require(isinstance(status, dict), "upgrade_status_invalid")
        secret = (status.get("controller") or {}).get("secret")
        if isinstance(secret, str) and secret:
            self.secrets.add(secret)
        self.evidence["lastStatus"] = {key: status.get(key) for key in
            ("phase", "errorCode", "desiredRunning", "tun", "tunDevice", "operation", "pid")}
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

    def require_auto_recovery(self, cursor: dict, original_name: str, status: dict,
                              process, evidence: dict, deadline: float) -> list[dict]:
        def complete():
            events = self.recovery_events(cursor)
            evidence["automaticRecoveryEvents"] = events
            require(len(events) <= 2, "upgrade_automatic_recovery_repeated")
            return events if len(events) == 2 else None

        events = wait_until(complete, "upgrade_automatic_recovery_evidence_missing", self.remaining(deadline))
        require([event["event"] for event in events] == list(AUTO_RECOVERY_EVENTS),
                "upgrade_automatic_recovery_event_order_invalid")
        initial, success = [event.get("data", {}) for event in events]
        for data in (initial, success):
            require(data.get("causeCode") == "tun_device_conflict"
                    and data.get("configRevision") == self.config_hash
                    and data.get("configuredName") == original_name
                    and data.get("effectiveName") == MANAGED_NAME
                    and type(data.get("attempt")) is int and data["attempt"] == 1
                    and data.get("previousExitConfirmed") is True
                    and type(data.get("generation")) is int and data["generation"] >= 0
                    and type(status.get("intentRevision")) is int
                    and data["generation"] == status["intentRevision"]
                    and data.get("desktopVersion") == self.owner.info["desktopVersion"]
                    and "coreVersion" in data, "upgrade_automatic_recovery_event_invalid")
        require(success["generation"] == initial["generation"] and success.get("corePid") == process.pid
                and isinstance(success["coreVersion"], str) and success["coreVersion"]
                and success["coreVersion"] == status.get("coreVersion")
                and type(events[0].get("processId")) is int and events[0]["processId"] > 0
                and events[0].get("processId") == events[1].get("processId")
                and events[0].get("timeMs", 0) > 0 and events[1].get("timeMs", 0) >= events[0]["timeMs"],
                "upgrade_automatic_recovery_success_identity_mismatch")
        evidence["automaticRecoveryEventCounts"] = {"after_start": len(events)}
        self.checkpoint()
        return events

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

    def running(self, name: str) -> tuple[dict, object]:
        status = self.status()
        require(status.get("phase") == "running" and status.get("statusStale") is not True,
                "upgrade_tun_not_running")
        require(status.get("tun") is True and status.get("systemProxy") is False
                and status.get("service", {}).get("running") is True, "upgrade_tun_not_service_owned")
        identity = self.request(status["controller"], "/desktop")
        require(identity.get("managed") is True and identity.get("state") == "running", "upgrade_core_not_managed")
        process = self.owner.processes.identity(identity.get("pid", 0))
        require(process and process.path == os.path.normcase(str(self.owner.core.resolve())), "upgrade_core_image_mismatch")
        self.retain_process(process)
        configuration = self.request(status["controller"], "/configs")
        require(configuration.get("tun", {}).get("enable") is True
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

    def assert_legacy(self, before: dict, evidence: dict, stage: str, *,
                      phantom: bool | None, allow_removed: bool = False) -> str:
        actual = self.native.query(before["name"], before["guid"])
        evidence.setdefault("legacyObservations", {})[stage] = actual
        self.checkpoint()
        if actual is None:
            require(allow_removed, "upgrade_legacy_adapter_disappeared_before_recovery")
            return "removed_by_native_driver_after_create"
        require(same_identity(before, actual) and (phantom is None or actual["phantom"] is phantom),
                "upgrade_legacy_adapter_renamed_adopted_or_changed")
        if phantom is False:
            require(actual["interface"] == before["interface"], "upgrade_live_foreign_interface_changed")
        return "same_exact_identity"

    def verify_managed(self, original_name: str, evidence: dict) -> tuple[dict, object]:
        status, process = self.running(MANAGED_NAME)
        self.require_mapping(status, original_name)
        self.unchanged_source(evidence, "after_automatic_start")
        adapter = self.capture(MANAGED_NAME, True, process)
        require(adapter["guid"] != expected_guid(original_name, exclusive=False), "upgrade_legacy_adapter_was_adopted")
        evidence["managedAdapter"] = adapter
        evidence["initialTunDevice"] = status["tunDevice"]
        evidence["persistedOverride"] = json.loads(self.preferences.read_text(encoding="utf-8"))["tunDeviceOverride"]
        evidence["sourceYamlSha256"] = self.config_hash
        return status, process

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

    def require_mapping(self, status: dict, original_name: str) -> None:
        device = status.get("tunDevice", {})
        require(device.get("configuredName") == original_name and device.get("effectiveName") == MANAGED_NAME
                and device.get("managed") is True and device.get("managedName") == MANAGED_NAME
                and device.get("configRevision") == self.config_hash,
                "upgrade_managed_tun_mapping_incorrect")
        preference = json.loads(self.preferences.read_text(encoding="utf-8"))
        require(preference.get("tunDeviceOverride") == {"configuredName": original_name, "effectiveName": MANAGED_NAME},
                "upgrade_managed_tun_mapping_not_persisted")

    def require_configured_name(self, status: dict, name: str) -> None:
        device = status.get("tunDevice", {})
        require(device.get("configuredName") == name and device.get("effectiveName") == name
                and device.get("managed") is False and device.get("managedName") == MANAGED_NAME
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
                with contextlib.suppress(Exception):
                    self.request(self.child_controller, "/desktop/shutdown", "POST")
                try:
                    self.child.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    self.child.kill()  # Popen retains the exact CreateProcess handle.
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
        self.native.require_unused(MANAGED_NAME)

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
        evidence["automaticMappingUsed"] = status.get("tunDevice", {}).get("managed") is True
        if evidence["automaticMappingUsed"]:
            require(observed is not None, "upgrade_hot_conflict_without_legacy_identity")
            status, current_core = self.verify_managed(name, evidence)
            events = self.require_auto_recovery(cursor, name, status, current_core, evidence, deadline)
        else:
            status, current_core = self.running(name)
            self.require_configured_name(status, name)
            evidence["candidateAdapter"] = self.capture(name, True, current_core)
            require(evidence["candidateAdapter"]["guid"] != legacy["guid"], "upgrade_hot_adopted_legacy_adapter")
            self.assert_recovery_events(cursor, [], evidence, "after_start")
            events = []
        evidence["candidateTunDevice"] = status["tunDevice"]
        evidence["legacyAfterCreate"] = self.assert_legacy(legacy, evidence, "after_automatic_start",
                                                          phantom=None, allow_removed=True)
        self.forwarding(evidence, "after_upgrade")
        self.unchanged_source(evidence, "after_upgrade_forwarding")
        self.assert_no_repair_controls(evidence)
        self.assert_recovery_events(cursor, events, evidence, "after_upgrade_forwarding")
        evidence.update({"sourceYamlSha256": self.config_hash, "servicePidBefore": before_service.pid,
                         "servicePidAfter": after_service.pid, "ownerSidPreserved": True})
        self.capture_logs("candidate_after_live_upgrade")
        self.owner.mark("published_0_2_5_live_tun_upgrade")
        self.finish_case()

    def isolated_old_core(self, name: str, *, auto_route: bool) -> dict:
        require(file_hash(self.old_core) == self.evidence["oldCoreSha256"], "upgrade_old_core_copy_changed")
        self.native.require_unused(name)
        home = self.directory / name
        home.mkdir()
        content = self.source(name).decode("utf-8").replace(
            "tun:\n  enable: false\n", "tun:\n  enable: true\n  auto-route: "
            + str(auto_route).lower() + "\n  auto-detect-interface: true\n")
        if not auto_route:
            # A live foreign owner should exercise only the name collision,
            # not duplicate IP addresses or a competing candidate /32 route.
            content = content.replace("198.18.0.1/16", "198.19.0.1/16").replace(
                "route-address: ['198.18.77.77/32']", "route-address: ['198.19.77.77/32']")
        (home / "config.yaml").write_text(content, encoding="utf-8")
        self.child_controller = {"url": "http://127.0.0.1:" + str(free_port()), "secret": secrets.token_urlsafe(32)}
        self.secrets.add(self.child_controller["secret"])
        environment = child_environment()
        environment["CLASH_OVERRIDE_SECRET"] = self.child_controller["secret"]
        self.child_identity = None
        self.child_record = None
        self.child_output = tempfile.TemporaryFile()
        self.child = subprocess.Popen([str(self.old_core), "-desktop-managed", "-d", str(home),
            "-f", str(home / "config.yaml"), "-ext-ctl", self.child_controller["url"].removeprefix("http://")],
            cwd=home, env=environment, stdin=subprocess.DEVNULL,
            stdout=self.child_output, stderr=self.child_output, close_fds=True,
            creationflags=subprocess.CREATE_NO_WINDOW)
        # Query the exact process returned by CreateProcess, not a fresh PID
        # lookup. Preserve its raw ProcessIdentity for the later kill guard;
        # only the comparison to the verified file needs path normalization.
        self.child_identity = self.owner.processes.from_handle(int(self.child._handle), self.child.pid)
        expected = os.path.normcase(str(self.old_core.resolve(strict=True)))
        self.child_record = {"pid": self.child.pid, "expectedPath": expected,
            "actualPath": self.child_identity.path if self.child_identity else None,
            "canonicalPath": None, "sameFile": False,
            "creationTime": str(self.child_identity.created) if self.child_identity else None,
            "exitCodeAtInspection": self.child.poll(), "exitCode": None}
        self.evidence.setdefault("isolatedProcesses", []).append(self.child_record)
        if self.child_identity:
            try:
                self.child_record["canonicalPath"] = os.path.normcase(str(
                    Path(self.child_identity.path).resolve(strict=True)))
                # samefile checks the volume/file ID on Windows. Different
                # textual paths never authorize a different executable.
                self.child_record["sameFile"] = os.path.samefile(self.child_identity.path, self.old_core)
            except OSError as error:
                self.child_record["pathQueryError"] = {"type": type(error).__name__,
                    "winerror": getattr(error, "winerror", None)}
        self.checkpoint()
        require(self.child_identity and self.child_record["canonicalPath"] == expected
                and self.child_record["sameFile"] is True,
                "upgrade_isolated_old_core_identity_mismatch")
        self.retain_process(self.child_identity)

        def ready():
            require(self.child.poll() is None, "upgrade_isolated_old_core_exited")
            try:
                value = self.request(self.child_controller, "/desktop")
            except (OSError, http.client.HTTPException):
                return None
            require(value.get("pid") == self.child.pid and value.get("managed") is True,
                    "upgrade_isolated_old_core_controller_mismatch")
            return value if value.get("state") == "running" else None

        wait_until(ready, "upgrade_isolated_old_core_start_timeout", 45)
        configuration = self.request(self.child_controller, "/configs")
        require(configuration.get("tun", {}).get("enable") is True
                and configuration["tun"].get("device") == name, "upgrade_isolated_old_tun_not_enabled")
        if not auto_route:
            tun = configuration["tun"]
            require(tun.get("auto-route") is False and tun.get("inet4-address") == ["198.19.0.1/30"]
                    and tun.get("route-address") == ["198.19.77.77/32"], "upgrade_foreign_tun_addresses_not_isolated")
        return self.capture(name, False, self.child_identity)

    @staticmethod
    def foreign_routes(adapter: dict) -> list[str]:
        index = adapter["interface"]["index"]
        require(type(index) is int and index > 0, "upgrade_foreign_interface_index_invalid")
        script = ("$ErrorActionPreference='Stop'; @((Get-NetRoute -AddressFamily IPv4 -InterfaceIndex "
                  + str(index) + ").DestinationPrefix) | ConvertTo-Json -Compress")
        result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True, text=True, encoding="utf-8", errors="strict", timeout=15,
            creationflags=subprocess.CREATE_NO_WINDOW)
        require(result.returncode == 0 and len(result.stdout) < 65536, "upgrade_foreign_routes_query_failed")
        value = json.loads(result.stdout.lstrip("\ufeff"))
        routes = value if isinstance(value, list) else [value]
        target = ipaddress.ip_address("198.18.77.77")
        require(routes and all(isinstance(prefix, str) and target not in ipaddress.ip_network(prefix)
                               for prefix in routes), "upgrade_foreign_route_claims_candidate_target")
        return routes

    def conflict_recovery(self, *, phantom: bool) -> None:
        label = "legacyPhantom" if phantom else "liveForeign"
        self.owner.stage = "upgrade_tun_" + label
        name = "MihomoUpgrade-" + uuid.uuid4().hex[:12]
        self.configure(self.source(name))
        evidence = self.evidence["scenarios"][label] = {"configuredName": name, "sourceYamlSha256": self.config_hash}
        self.unchanged_source(evidence, "before_fixture_start")
        legacy = evidence["legacyAdapter"] = self.isolated_old_core(name, auto_route=phantom)
        evidence["oldCorePid"] = self.child.pid
        if phantom:
            self.forwarding(evidence, "before_forced_exit")
            self.owner.processes.kill(self.child_identity)
            require(self.child.wait(timeout=10) != 0, "upgrade_legacy_owner_not_force_killed")
            require(self.exited(self.child_identity),
                    "upgrade_legacy_owner_exit_unconfirmed")

            def residual():
                actual = self.native.query(name, legacy["guid"])
                return actual if actual and actual["phantom"] and actual["interface"] else None

            witnessed = wait_until(residual, "upgrade_legacy_crash_did_not_leave_real_alias_phantom", 10)
            require(same_identity(legacy, witnessed), "upgrade_legacy_crash_identity_changed")
            evidence["witnessedPhantom"] = witnessed
            evidence["oldProcessExitConfirmed"] = True
            # No other Wintun call occurs between this read-only witness and
            # the candidate's first start. In particular, no Open/dummy
            # Create is used to inspect or manufacture the crash residue.
            self.checkpoint()
        else:
            evidence["foreignRoutes"] = self.foreign_routes(legacy)
        evidence["beforeAutomaticStart"] = self.assert_legacy(legacy, evidence, "before_automatic_start",
                                                             phantom=phantom)
        self.native.require_unused(MANAGED_NAME)
        cursor = self.recovery_cursor()
        deadline = time.monotonic() + START_TIMEOUT
        self.launch_gui(label)
        self.start_gui(evidence, "initial", deadline=deadline)
        status, process = self.verify_managed(name, evidence)
        events = self.require_auto_recovery(cursor, name, status, process, evidence, deadline)
        if not phantom:
            require(self.child.poll() is None, "upgrade_live_foreign_owner_exited")
        evidence["afterAutomaticStart"] = self.assert_legacy(legacy, evidence, "after_automatic_start",
                                                            phantom=phantom, allow_removed=phantom)
        self.forwarding(evidence, "after_automatic_start")
        self.assert_no_repair_controls(evidence)
        if phantom:
            self.driver.invoke("desktop_reload_config", {"force": True}, timeout=95)
            status, creator = self.running(MANAGED_NAME)
            self.capture(MANAGED_NAME, True, creator)
            self.require_mapping(status, name)
            evidence["tunDeviceAfterReload"] = status["tunDevice"]
            self.unchanged_source(evidence, "after_reload")
            self.forwarding(evidence, "after_reload")
            self.assert_recovery_events(cursor, events, evidence, "after_reload")
            self.close_gui()
            self.unchanged_source(evidence, "after_stop")
            self.launch_gui("persisted-mapping")
            self.start_gui(evidence, "relaunch")
            status, creator = self.running(MANAGED_NAME)
            self.require_mapping(status, name)
            evidence["tunDeviceAfterRelaunch"] = status["tunDevice"]
            self.capture(MANAGED_NAME, True, creator)
            self.unchanged_source(evidence, "after_relaunch")
            self.forwarding(evidence, "after_relaunch")
            self.assert_recovery_events(cursor, events, evidence, "after_relaunch")
            evidence["mappingSurvivedReloadAndRelaunch"] = True
            self.owner.mark("managed_tun_mapping_persists_without_yaml_rewrite")
        else:
            require(self.child.poll() is None, "upgrade_live_foreign_owner_not_preserved")
            evidence["afterForwarding"] = self.assert_legacy(legacy, evidence, "after_forwarding", phantom=False)
            self.unchanged_source(evidence, "after_forwarding")
            self.assert_recovery_events(cursor, events, evidence, "after_forwarding")
        self.capture_logs(label)
        self.owner.mark("legacy_tun_phantom_automatic_recovery" if phantom else "live_foreign_tun_preserved")
        self.finish_case()

    def managed_name_collision(self) -> None:
        self.owner.stage = "upgrade_managed_tun_collision_bounded"
        self.configure(self.source(MANAGED_NAME))
        evidence = self.evidence["scenarios"]["managedNameCollision"] = {
            "configuredName": MANAGED_NAME, "sourceYamlSha256": self.config_hash}
        self.unchanged_source(evidence, "before_fixture_start")
        legacy = evidence["legacyAdapter"] = self.isolated_old_core(MANAGED_NAME, auto_route=False)
        evidence["oldCorePid"] = self.child.pid
        evidence["foreignRoutes"] = self.foreign_routes(legacy)

        def name_family() -> list[dict]:
            return sorted((row for row in self.native.interfaces()
                           if row["name"].casefold().startswith(MANAGED_NAME.casefold())),
                          key=lambda row: (row["name"], row["guid"], row["luid"]))

        before = evidence["nameFamilyBefore"] = name_family()
        require(before == [legacy["interface"]], "upgrade_managed_name_fixture_not_isolated")
        require(self.native.query(MANAGED_NAME, expected_guid(MANAGED_NAME, exclusive=True)) is None,
                "upgrade_managed_product_instance_already_exists")
        cursor = self.recovery_cursor()
        deadline = time.monotonic() + START_TIMEOUT
        self.launch_gui("managed-name-collision")
        status = self.start_gui(evidence, "initial", expect_conflict=True, deadline=deadline)
        self.require_configured_name(status, MANAGED_NAME)
        evidence["tunDeviceAtConflict"] = status["tunDevice"]
        self.unchanged_source(evidence, "after_rejected_start")
        self.assert_recovery_events(cursor, [], evidence, "after_rejected_start")
        evidence["afterRejectedStart"] = self.assert_legacy(legacy, evidence, "after_rejected_start", phantom=False)

        # Cover multiple normal service poll ticks under the original total
        # deadline. A failed fixed-name start must stay stopped; neither a new
        # suffixed interface nor another core generation is allowed to appear.
        observation_started = time.monotonic()
        observation_end = observation_started + 6
        require(observation_end < deadline, "upgrade_conflict_left_no_stability_budget")
        evidence["terminalObservations"] = []
        while time.monotonic() < observation_end:
            status = self.status(timeout=min(5, self.remaining(deadline)))
            record = {key: status.get(key) for key in
                      ("phase", "errorCode", "operation", "pid", "recoveryGeneration", "statusStale")}
            evidence["terminalObservations"].append(record)
            require(status.get("phase") in ("failed", "error") and status.get("errorCode") == "tun_device_conflict"
                    and status.get("operation") is None and status.get("pid") is None
                    and status.get("controller") is None and status.get("statusStale") is not True,
                    "upgrade_managed_collision_restarted_or_not_terminal")
            require(self.child.poll() is None and not self.exited(self.child_identity),
                    "upgrade_managed_collision_foreign_owner_exited")
            require(not self.owner.processes.at_paths([self.owner.core]), "upgrade_managed_collision_core_respawned")
            require(name_family() == before, "upgrade_managed_collision_created_suffix_or_changed_owner")
            time.sleep(min(.15, max(0, observation_end - time.monotonic()), self.remaining(deadline)))
        evidence["terminalObservationMilliseconds"] = round((time.monotonic() - observation_started) * 1000)
        evidence["nameFamilyAfter"] = name_family()
        require(self.native.query(MANAGED_NAME, expected_guid(MANAGED_NAME, exclusive=True)) is None,
                "upgrade_managed_collision_created_product_instance")
        self.require_configured_name(status, MANAGED_NAME)
        self.assert_recovery_events(cursor, [], evidence, "after_stability_observation")
        self.unchanged_source(evidence, "after_stability_observation")
        evidence["afterObservation"] = self.assert_legacy(legacy, evidence, "after_stability_observation", phantom=False)
        self.assert_no_repair_controls(evidence, offline=True)
        self.capture_logs("managed-name-collision")
        self.owner.mark("managed_tun_collision_bounded")
        self.finish_case()

    def run(self) -> None:
        try:
            require(self.owner.info.get("coreCommit") == CURRENT_CORE_SOURCE, "upgrade_candidate_core_pin_changed")
            self.native.require_unused(MANAGED_NAME)
            self.desktop = RestrictedDesktop()
            require(self.desktop.sid == self.owner.sid, "upgrade_gui_owner_sid_changed")
            self.socks = SocksFixture()
            self.hot_upgrade()
            self.conflict_recovery(phantom=True)
            self.conflict_recovery(phantom=False)
            self.managed_name_collision()
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
        attempt("isolated_old_child_stop", self.stop_child)
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
