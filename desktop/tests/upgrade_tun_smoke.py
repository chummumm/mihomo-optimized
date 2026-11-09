"""Actual 0.2.5 TUN upgrade and deliberate desktop-name recovery acceptance.

The hot upgrade uses the installed published GUI, host, service and core. The
crash/foreign cases use a verified copy of that same old core as an isolated
child, so an old supervisor cannot restart Wintun and erase the evidence before
the candidate sees it. All recovery choices go through the visible WPF button.
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
from urllib.parse import urlsplit
import uuid

from upgrade_tun_native import MANAGED_NAME, NativeTunIdentity, expected_guid, same_identity
from windows_smoke import (
    NativeDriver, RestrictedDesktop, SmokeFailure, SocksFixture, child_environment,
    free_port, profile, require, safe_code, wait_until,
)


OLD_CORE_SOURCE = "96c428bce6b1e7d68327910dad5f426ad29d2d98"
CURRENT_CORE_SOURCE = "e348a0f888b1ae497fb45c87a769bb479ca37e1c"


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

    def unchanged_source(self) -> None:
        require(file_hash(self.owner.config) == self.config_hash, "upgrade_tun_source_yaml_changed")

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

    def status(self) -> dict:
        status = self.driver.invoke("desktop_status", timeout=5)
        require(isinstance(status, dict), "upgrade_status_invalid")
        secret = (status.get("controller") or {}).get("secret")
        if isinstance(secret, str) and secret:
            self.secrets.add(secret)
        self.evidence["lastStatus"] = {key: status.get(key) for key in
            ("phase", "errorCode", "desiredRunning", "tun", "tunDevice")}
        return status

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

    def start_gui(self, *, allow_conflict: bool = False, automatic: bool = False) -> dict:
        if not automatic or self.status().get("phase") == "stopped":
            try:
                self.driver.invoke("desktop_start", timeout=95)
            except SmokeFailure as error:
                if not allow_conflict or str(error) != "native_tun_device_conflict":
                    raise

        def terminal():
            status = self.status()
            if status.get("errorCode") == "tun_device_conflict":
                require(allow_conflict, "upgrade_unexpected_tun_conflict")
                return status
            if status.get("phase") == "running":
                return status
            require(status.get("phase") not in ("failed", "error"),
                    "upgrade_start_failed_" + safe_code(status.get("errorCode")))
            return None

        return wait_until(terminal, "upgrade_tun_start_timeout", 45)

    def running(self, name: str) -> tuple[dict, object]:
        status = wait_until(lambda: (value if (value := self.status()).get("phase") == "running" else None),
                            "upgrade_tun_not_running", 45)
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
            wait_until(self.socks.roundtrip, "upgrade_tun_forwarding_failed_" + label, 25)
        finally:
            evidence.setdefault("forwarding", {})[label] = self.socks.diagnostics()
            self.checkpoint()
        require(evidence["forwarding"][label]["forwarded"] > before, "upgrade_tun_forwarding_counter_did_not_advance")

    def assert_legacy(self, before: dict, *, phantom: bool | None, allow_removed: bool = False) -> str:
        actual = self.native.query(before["name"], before["guid"])
        if actual is None:
            require(allow_removed, "upgrade_legacy_adapter_disappeared_before_choice")
            return "removed_by_native_driver_after_create"
        require(same_identity(before, actual) and (phantom is None or actual["phantom"] is phantom),
                "upgrade_legacy_adapter_renamed_adopted_or_changed")
        if phantom is False:
            require(actual["interface"] == before["interface"], "upgrade_live_foreign_interface_changed")
        return "same_exact_identity"

    def choose_managed(self, original_name: str, evidence: dict) -> tuple[dict, object]:
        status = self.status()
        device = status.get("tunDevice", {})
        require(status.get("errorCode") == "tun_device_conflict" and device.get("canUseManaged") is True
                and device.get("managed") is False and device.get("configuredName") == original_name
                and device.get("effectiveName") == original_name and device.get("managedName") == MANAGED_NAME
                and device.get("configRevision") == self.config_hash, "upgrade_typed_tun_conflict_missing")
        self.unchanged_source()
        self.native.require_unused(MANAGED_NAME)
        self.driver.navigate("home")

        def visible_choice():
            choice = self.driver.ui("HomeUseManagedTunDevice")
            return choice if choice.get("found") and choice.get("enabled") and not choice.get("offscreen") else None

        choice = wait_until(visible_choice, "upgrade_managed_tun_ui_choice_unavailable", 12)
        require(choice.get("name") == "改用桌面专属网卡并重试", "upgrade_managed_tun_ui_label_mismatch")
        require(self.driver.ui("HomeUseManagedTunDevice", "invoke").get("invoked"),
                "upgrade_managed_tun_ui_choice_not_invoked")
        status, process = self.running(MANAGED_NAME)
        self.require_mapping(status, original_name)
        self.unchanged_source()
        adapter = self.capture(MANAGED_NAME, True, process)
        require(adapter["guid"] != expected_guid(original_name, exclusive=False), "upgrade_legacy_adapter_was_adopted")
        evidence["managedAdapter"] = adapter
        evidence["uiChoice"] = "HomeUseManagedTunDevice"
        evidence["sourceYamlSha256"] = self.config_hash
        self.forwarding(evidence, "after_choice")
        return status, process

    def require_mapping(self, status: dict, original_name: str) -> None:
        device = status.get("tunDevice", {})
        require(device.get("configuredName") == original_name and device.get("effectiveName") == MANAGED_NAME
                and device.get("managed") is True and device.get("configRevision") == self.config_hash,
                "upgrade_managed_tun_mapping_incorrect")
        preference = json.loads(self.preferences.read_text(encoding="utf-8"))
        require(preference.get("tunDeviceOverride") == {"configuredName": original_name, "effectiveName": MANAGED_NAME},
                "upgrade_managed_tun_mapping_not_persisted")

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
        evidence = self.evidence["scenarios"]["hotUpgrade"] = {"configuredName": name}
        self.launch_gui("old-live")
        self.start_gui()
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
        after_service = self.owner.running_service()
        require(after_service != before_service and self.owner.owner_file.read_bytes() == owner_bytes,
                "upgrade_tun_service_handoff_failed")
        self.owner.assert_candidate_files(configuration_hash=self.config_hash)
        self.launch_gui("candidate-hot")
        status = self.start_gui(allow_conflict=True, automatic=True)
        evidence["manualChoiceNeeded"] = status.get("errorCode") == "tun_device_conflict"
        if evidence["manualChoiceNeeded"]:
            observed = self.native.query(name, legacy["guid"])
            require(observed and same_identity(legacy, observed), "upgrade_hot_conflict_not_original_identity")
            evidence["legacyAtConflict"] = observed
            self.choose_managed(name, evidence)
            evidence["legacyAfterCreate"] = self.assert_legacy(legacy, phantom=None, allow_removed=True)
        else:
            _, current_core = self.running(name)
            evidence["candidateAdapter"] = self.capture(name, True, current_core)
            require(evidence["candidateAdapter"]["guid"] != legacy["guid"], "upgrade_hot_adopted_legacy_adapter")
            self.forwarding(evidence, "after_upgrade")
        self.unchanged_source()
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
            # the candidate's rejected start. In particular, no Open/dummy
            # Create is used to inspect or manufacture the crash residue.
            self.checkpoint()
        else:
            evidence["foreignRoutes"] = self.foreign_routes(legacy)
        self.launch_gui(label)
        status = self.start_gui(allow_conflict=True)
        require(status.get("errorCode") == "tun_device_conflict", "upgrade_legacy_collision_not_rejected")
        evidence["beforeChoice"] = self.assert_legacy(legacy, phantom=phantom)
        if not phantom:
            require(self.child.poll() is None, "upgrade_live_foreign_owner_exited")
        self.choose_managed(name, evidence)
        evidence["afterChoice"] = self.assert_legacy(legacy, phantom=phantom, allow_removed=phantom)
        if phantom:
            self.driver.invoke("desktop_reload_config", {"force": True}, timeout=95)
            status, creator = self.running(MANAGED_NAME)
            self.capture(MANAGED_NAME, True, creator)
            self.require_mapping(status, name)
            self.unchanged_source()
            self.forwarding(evidence, "after_reload")
            self.close_gui()
            self.launch_gui("persisted-choice")
            self.start_gui(automatic=True)
            status, creator = self.running(MANAGED_NAME)
            self.require_mapping(status, name)
            self.capture(MANAGED_NAME, True, creator)
            self.unchanged_source()
            self.forwarding(evidence, "after_relaunch")
            evidence["mappingSurvivedReloadAndRelaunch"] = True
            self.owner.mark("managed_tun_choice_persists_without_yaml_rewrite")
        else:
            require(self.child.poll() is None, "upgrade_live_foreign_owner_not_preserved")
            evidence["afterForwarding"] = self.assert_legacy(legacy, phantom=False)
        self.capture_logs(label)
        self.owner.mark("legacy_tun_phantom_ui_recovery" if phantom else "live_foreign_tun_preserved")
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
