#!/usr/bin/env python3
"""Exercise the published 0.1.1 uninstaller and the candidate NSIS installer.

Only disposable GitHub Actions Windows runners may run this elevated fixture.
A read handle without FILE_SHARE_DELETE reproduces a leftover old helper after
an otherwise successful uninstall. No product ACL or service permission is
changed by the fixture. The subsequent windows_smoke.py still exercises the
actual GUI with its same-SID, non-elevated token.

NSIS requires the unquoted _?= directory to be the final command-line argument:
https://nsis.sourceforge.io/Docs/Chapter3.html#installerusage
"""

from __future__ import annotations

import contextlib
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
from urllib.request import Request, urlopen
import uuid
import zipfile

from windows_smoke import (
    Processes, SERVICE_NAME, SmokeFailure, child_environment,
    owned_firewall_rule, owned_firewall_rule_count, require, safe_code, wait_until,
)


PRODUCT = "Mihomo Optimized"
TARGET = "x86_64-pc-windows-msvc"
LEGACY_VERSION = "0.1.1"
LEGACY_SOURCE = "e2597a790cccb807de2b1ac58d3ba62346f94f7b"
LEGACY_URL = (
    "https://github.com/chummumm/mihomo-optimized/releases/download/"
    "mihomo-desktop-v0.1.1/mihomo-desktop-0.1.1-windows-x64-setup.exe"
)
LEGACY_SHA256 = "64326bf2d2672cfc76fbb1c1dbd25138a438ee0e78751ab72ac2a91a62f84690"
EXECUTABLES = (
    "mihomo-optimized.exe", "mihomo-core.exe", "mihomo-desktop-service.exe",
    "mihomo-update-verify.exe",
)
UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mihomo Optimized"


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def failure_code(error: Exception) -> str:
    if isinstance(error, SmokeFailure):
        return safe_code(str(error))
    code = "unexpected_" + type(error).__name__
    if isinstance(getattr(error, "winerror", None), int):
        code += "_winerror_" + str(error.winerror)
    return code


def log_tail(stream) -> str:
    stream.seek(0, os.SEEK_END)
    stream.seek(max(0, stream.tell() - 4096))
    text = stream.read().decode("utf-8", errors="replace")
    return "".join(char for char in text if char in "\n\t" or ord(char) >= 32)


def uninstall_command(uninstaller: Path) -> str:
    # list2cmdline would quote the whole _?= argument and change NSIS parsing.
    require('"' not in str(uninstaller), "invalid_uninstaller_path")
    return f'"{uninstaller}" /S _?={uninstaller.parent}'


def executable_path(value: str) -> str:
    """Normalize the single local executable SCM stores, never a shell command."""
    require(isinstance(value, str) and bool(value) and "\0" not in value, "service_binary_path_invalid")
    if value.startswith('"'):
        require(value.endswith('"') and value.count('"') == 2, "service_binary_arguments_unexpected")
        value = value[1:-1]
    require('"' not in value, "service_binary_arguments_unexpected")
    if value.startswith("\\\\?\\"):
        value = value[4:]
    require(re.match(r"^[a-zA-Z]:\\", value), "service_binary_not_local_absolute_path")
    return os.path.normcase(str(Path(value).resolve()))


def registration() -> dict | None:
    import winreg

    try:
        key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, UNINSTALL_KEY, 0,
                             winreg.KEY_READ | winreg.KEY_WOW64_64KEY)
    except FileNotFoundError:
        return None
    with key:
        return {name: winreg.QueryValueEx(key, name)[0]
                for name in ("DisplayVersion", "InstallLocation", "UninstallString")}


def service_snapshot() -> dict:
    """Close every SCM handle before polling for actual deletion (error 1060)."""
    import pywintypes
    import win32service

    manager = win32service.OpenSCManager(None, None, win32service.SC_MANAGER_CONNECT)
    try:
        try:
            service = win32service.OpenService(
                manager, SERVICE_NAME,
                win32service.SERVICE_QUERY_STATUS | win32service.SERVICE_QUERY_CONFIG,
            )
        except pywintypes.error as error:
            if error.winerror in (1060, 1072):
                return {"installed": error.winerror != 1060, "winError": error.winerror}
            raise
        try:
            status = win32service.QueryServiceStatusEx(service)
            config = win32service.QueryServiceConfig(service)
            return {"installed": True, "state": status["CurrentState"],
                    "pid": status["ProcessId"], "binary": config[3], "account": config[7]}
        finally:
            win32service.CloseServiceHandle(service)
    finally:
        win32service.CloseServiceHandle(manager)


class UpgradeAcceptance:
    def __init__(self, artifacts: Path) -> None:
        self.artifacts = artifacts
        self.install_dir = Path(os.environ["ProgramFiles"]) / PRODUCT
        self.app, self.core, self.helper, self.verifier = [self.install_dir / name for name in EXECUTABLES]
        self.config = Path(os.environ["LOCALAPPDATA"]) / PRODUCT / "config.yaml"
        self.owner_file = Path(os.environ["PROGRAMDATA"]) / PRODUCT / "service" / "owner.sid"
        self.processes = Processes()
        self.service_owned = False
        self.mutations_started = False
        self.active_children = []
        self.file_lock = None
        self.marker_hash = None
        self.stage = "preflight"
        self.report = {"passed": False, "platform": "windows", "tests": {},
                       "commands": [], "diagnostics": [],
                       "baseline": {"version": LEGACY_VERSION, "source": LEGACY_SOURCE,
                                    "url": LEGACY_URL, "sha256": LEGACY_SHA256}}

    def write_report(self) -> None:
        temporary = self.artifacts / "upgrade.json.tmp"
        temporary.write_text(json.dumps(self.report, indent=2) + "\n", encoding="utf-8")
        temporary.replace(self.artifacts / "upgrade.json")

    def mark(self, name: str) -> None:
        self.report["tests"][name] = True
        self.write_report()
        print("PASS: upgrade_" + name, flush=True)

    def command(self, name: str, executable: Path, arguments: list[str] | str,
                timeout: float = 90) -> dict:
        record = {"name": name, "executable": executable.name, "timeoutSeconds": timeout,
                  "exitCode": None, "timedOut": False}
        self.report["commands"].append(record)
        self.write_report()
        process = None
        started = time.monotonic()
        # File-backed output cannot leave communicate() waiting for an inherited
        # pipe after the parent exits. Only the final 4 KiB enters the evidence.
        with tempfile.TemporaryFile() as output, tempfile.TemporaryFile() as errors:
            try:
                process = subprocess.Popen(
                    arguments if isinstance(arguments, str) else [str(executable), *arguments],
                    executable=str(executable), shell=False, cwd=executable.parent,
                    env=child_environment(), stdin=subprocess.DEVNULL,
                    stdout=output, stderr=errors, close_fds=True,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
                self.active_children.append(process)
                record["pid"] = process.pid
                process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                record["timedOut"] = True
                self.stop_child(process)
                raise SmokeFailure(name + "_timeout") from None
            finally:
                if process is not None:
                    record["exitCode"] = process.poll()
                    if process.returncode is not None and process in self.active_children:
                        self.active_children.remove(process)
                record["durationSeconds"] = round(time.monotonic() - started, 2)
                record["stdout"] = log_tail(output)
                record["stderr"] = log_tail(errors)
                self.write_report()
        return record

    def stop_child(self, process) -> None:
        if process is None or process.poll() is not None:
            return
        root = self.processes.identity(process.pid)
        if root:
            # nsExec can have one bundled helper still running. Never walk into
            # runner/Node or unrelated processes; every parent edge is verified.
            allowed = {os.path.normcase(str(self.helper.resolve()))}
            for child in self.processes.descendants([root], allowed):
                self.processes.kill(child)
        process.kill()  # Popen retains the exact process handle, not a reused PID.
        process.wait(timeout=10)

    def load_candidate(self) -> None:
        distribution = Path("artifacts/desktop").resolve()
        self.info = json.loads((distribution / "build-info.json").read_text(encoding="utf-8-sig"))
        version = self.info.get("desktopVersion", "")
        require(re.fullmatch(r"\d+\.\d+\.\d+", version), "candidate_version_invalid")
        require(tuple(map(int, version.split("."))) > (0, 1, 1), "candidate_must_follow_0_1_1")
        require(self.info.get("target") == TARGET
                and self.info.get("repository") == "chummumm/mihomo-optimized"
                and re.fullmatch(r"[0-9a-f]{40}", self.info.get("uiCommit", ""))
                and re.fullmatch(r"[0-9a-f]{40}", self.info.get("coreCommit", "")),
                "candidate_source_identity_invalid")
        prefix = "mihomo-desktop-" + version + "-windows-x64"
        self.candidate = distribution / (prefix + "-setup.exe")
        archive = distribution / (prefix + "-portable.zip")
        require(list(distribution.glob("*-setup.exe")) == [self.candidate], "candidate_installer_ambiguous")
        checksums = {}
        for line in (distribution / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
            match = re.fullmatch(r"([0-9a-f]{64})  ([^/\\]+)", line)
            require(match and match[2] not in checksums, "candidate_checksum_manifest_invalid")
            checksums[match[2]] = match[1]
        for path in (self.candidate, archive, distribution / "build-info.json"):
            require(path.is_file() and checksums.get(path.name) == digest(path), "candidate_checksum_mismatch")
        self.expected_hashes = {}
        self.expected_sizes = {}
        portable_hashes = {}
        marker_patch = {}
        with zipfile.ZipFile(archive) as bundle:
            info_member = PRODUCT + "/resources/build-info.json"
            require(bundle.namelist().count(info_member) == 1
                    and bundle.getinfo(info_member).file_size < 65536, "portable_build_info_invalid")
            require(json.loads(bundle.read(info_member)) == self.info, "portable_source_mismatch")
            for name in EXECUTABLES:
                member = PRODUCT + "/" + name
                require(bundle.namelist().count(member) == 1
                        and 0 < bundle.getinfo(member).file_size < 256 * 1024 * 1024,
                        "portable_binary_invalid")
                with bundle.open(member) as stream:
                    if name == "mihomo-optimized.exe":
                        data = stream.read()
                        portable_hashes[name] = hashlib.sha256(data).hexdigest()
                        unknown = b"__TAURI_BUNDLE_TYPE_VAR_UNK"
                        nsis = b"__TAURI_BUNDLE_TYPE_VAR_NSS"
                        require(data.count(unknown) == 1 and data.count(nsis) == 0,
                                "portable_gui_bundle_marker_invalid")
                        # Locked tauri-cli 2.12.3 patches the first UNK marker for
                        # NSIS, then restores the original build for other bundles.
                        # https://github.com/tauri-apps/tauri/blob/tauri-cli-v2.12.3/crates/tauri-bundler/src/bundle.rs
                        # Hash the complete expected installed file; no bytes are
                        # excluded from the subsequent strict installed comparison.
                        self.expected_hashes[name] = hashlib.sha256(data.replace(unknown, nsis, 1)).hexdigest()
                        marker_patch = {"binary": name, "tauriCliVersion": "2.12.3",
                                        "from": unknown.decode("ascii"), "to": nsis.decode("ascii"),
                                        "offset": data.index(unknown), "replacements": 1}
                    else:
                        self.expected_hashes[name] = hashlib.file_digest(stream, "sha256").hexdigest()
                        portable_hashes[name] = self.expected_hashes[name]
                self.expected_sizes[name] = bundle.getinfo(member).file_size
            for name in EXECUTABLES[1:]:
                companion = name.removesuffix(".exe") + "-" + TARGET + ".exe"
                require(self.info.get("companions", {}).get(companion) == self.expected_hashes[name],
                        "portable_companion_hash_mismatch")
        self.report["candidate"] = {"version": version, "source": self.info["uiCommit"],
                                    "coreSource": self.info["coreCommit"],
                                    "installer": self.candidate.name, "sha256": digest(self.candidate),
                                    "portableBinarySha256": portable_hashes,
                                    "installedBinarySha256": self.expected_hashes,
                                    "markerPatch": marker_patch}

    def download_legacy(self) -> Path:
        # Keep this outside artifacts/desktop so the candidate remains unique.
        destination = Path("artifacts/legacy").resolve() / "mihomo-desktop-0.1.1-windows-x64-setup.exe"
        destination.parent.mkdir(parents=True, exist_ok=True)
        if not destination.exists():
            temporary = destination.with_suffix(".exe.part")
            started = time.monotonic()
            try:
                request = Request(LEGACY_URL, headers={"User-Agent": "mihomo-upgrade-acceptance"})
                with urlopen(request, timeout=30) as response, temporary.open("wb") as output:
                    size = 0
                    while chunk := response.read(1024 * 1024):
                        size += len(chunk)
                        require(size <= 64 * 1024 * 1024, "legacy_download_too_large")
                        require(time.monotonic() - started < 180, "legacy_download_timeout")
                        output.write(chunk)
                require(digest(temporary) == LEGACY_SHA256, "legacy_download_checksum_mismatch")
                temporary.replace(destination)
            finally:
                temporary.unlink(missing_ok=True)
        require(digest(destination) == LEGACY_SHA256, "legacy_installer_checksum_mismatch")
        return destination

    def assert_registration(self, version: str) -> None:
        entry = registration()
        require(entry and entry["DisplayVersion"] == version, "installed_registry_version_mismatch")
        require(os.path.normcase(entry["InstallLocation"].strip('"')) == os.path.normcase(str(self.install_dir))
                and os.path.normcase(entry["UninstallString"].strip('"'))
                == os.path.normcase(str(self.install_dir / "uninstall.exe")), "installed_registry_path_mismatch")

    def assert_marker(self) -> None:
        require(self.config.is_file() and digest(self.config) == self.marker_hash, "user_configuration_not_preserved")

    def assert_no_service(self) -> dict:
        result = wait_until(lambda: (value if (value := service_snapshot()).get("winError") == 1060 else None),
                            "service_not_deleted", 45)
        require(owned_firewall_rule_count(self.core) == 0, "uninstalled_firewall_rule_remains")
        return result

    def running_service(self):
        import win32api
        import win32security
        import win32service

        state = wait_until(lambda: (value if (value := service_snapshot()).get("state")
                                    == win32service.SERVICE_RUNNING else None), "service_not_running", 45)
        require(executable_path(state["binary"]) == os.path.normcase(str(self.helper.resolve())),
                "owned_service_binary_mismatch")
        require(state["account"].lower() == "localsystem", "owned_service_account_mismatch")
        identity = self.processes.identity(state["pid"])
        require(identity and identity.path == os.path.normcase(str(self.helper.resolve())),
                "owned_service_process_mismatch")
        process = win32api.OpenProcess(0x1000, False, identity.pid)
        try:
            token = win32security.OpenProcessToken(process, win32security.TOKEN_QUERY)
            try:
                user = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
                require(win32security.ConvertSidToStringSid(user) == "S-1-5-18", "owned_service_not_system")
            finally:
                token.Close()
        finally:
            process.Close()
        require(self.owner_file.read_text(encoding="utf-8").strip() == self.sid, "service_owner_not_preserved")
        return identity

    def install_service(self, label: str):
        require(service_snapshot().get("winError") == 1060, "service_already_installed")
        self.service_owned = True  # Also covers a helper failure after CreateService.
        result = self.command(label, self.helper, ["--install", "--owner-sid", self.sid])
        require(result["exitCode"] == 0, label + "_failed")
        identity = self.running_service()
        require(owned_firewall_rule_count(self.core) == 1, "installed_firewall_rule_missing")
        return identity

    def assert_candidate_files(self) -> None:
        evidence = {"stage": self.stage, "files": {}}
        for name, expected in self.expected_hashes.items():
            path = self.install_dir / name
            item = {"expectedSha256": expected, "expectedSize": self.expected_sizes[name],
                    "actualSha256": None, "actualSize": None, "missing": not path.is_file()}
            evidence["files"][name] = item
            if not item["missing"]:
                try:
                    item["actualSize"] = path.stat().st_size
                    item["actualSha256"] = digest(path)
                except OSError as error:
                    item["readError"] = failure_code(error)
        # Preserve all four observations before the first strict assertion can
        # fail, including a stale GUI alongside otherwise current companions.
        self.report.setdefault("installedFileChecks", []).append(evidence)
        self.write_report()
        installed = json.loads((self.install_dir / "resources/build-info.json").read_text(encoding="utf-8-sig"))
        evidence["buildInfoMatches"] = installed == self.info
        self.write_report()
        require(installed == self.info, "installed_build_source_mismatch")
        for name, item in evidence["files"].items():
            require(not item["missing"] and item["actualSha256"] == item["expectedSha256"]
                    and item["actualSize"] == item["expectedSize"], "installed_binary_mismatch_" + name)
        self.assert_registration(self.info["desktopVersion"])
        self.assert_marker()

    def run(self) -> None:
        import win32api
        import win32con
        import win32file
        import win32security

        require(not self.install_dir.exists() and registration() is None, "preexisting_application_installation")
        require(service_snapshot().get("winError") == 1060, "preexisting_mihomo_service")
        require(not self.processes.at_paths([self.app, self.core, self.helper]), "preexisting_mihomo_process")
        require(not self.config.parent.exists() and not self.owner_file.parent.exists(), "preexisting_mihomo_data")
        require(owned_firewall_rule_count(self.core) == 0, "preexisting_mihomo_firewall_rule")
        token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32security.TOKEN_QUERY)
        try:
            self.sid = win32security.ConvertSidToStringSid(win32security.GetTokenInformation(token, win32security.TokenUser)[0])
        finally:
            token.Close()
        self.load_candidate()
        self.stage = "legacy_download"
        legacy = self.download_legacy()
        self.mark("published_baseline_verified")

        self.stage = "legacy_install"
        self.mutations_started = True
        require(self.command("legacy_install", legacy, ["/S"], 180)["exitCode"] == 0, "legacy_install_failed")
        self.assert_registration(LEGACY_VERSION)
        info = json.loads((self.install_dir / "resources/build-info.json").read_text(encoding="utf-8-sig"))
        require(info.get("desktopVersion") == LEGACY_VERSION and info.get("uiCommit") == LEGACY_SOURCE,
                "installed_legacy_source_mismatch")
        legacy_helper_hash = digest(self.helper)
        require(info.get("companions", {}).get("mihomo-desktop-service-" + TARGET + ".exe") == legacy_helper_hash,
                "installed_legacy_helper_mismatch")
        self.config.parent.mkdir(parents=True, exist_ok=True)
        with self.config.open("xb") as marker:
            marker.write(("# upgrade-smoke-" + uuid.uuid4().hex + "\nmode: rule\nrules:\n  - MATCH,DIRECT\n").encode("utf-8"))
        self.marker_hash = digest(self.config)
        self.report["configurationMarkerSha256"] = self.marker_hash
        legacy_service = self.install_service("legacy_service_install")
        self.report["baseline"]["servicePid"] = legacy_service.pid
        self.mark("legacy_service_running")

        self.stage = "legacy_uninstall_with_locked_helper"
        self.file_lock = win32file.CreateFile(
            str(self.helper), win32con.GENERIC_READ,
            win32con.FILE_SHARE_READ | win32con.FILE_SHARE_WRITE,
            None, win32con.OPEN_EXISTING, win32con.FILE_ATTRIBUTE_NORMAL, None,
        )
        try:
            uninstaller = self.install_dir / "uninstall.exe"
            raw = uninstall_command(uninstaller)
            self.report["legacyUninstall"] = {"commandLine": raw, "fileShareDelete": False}
            result = self.command("legacy_uninstall", uninstaller, raw, 120)
            require(result["exitCode"] == 0, "legacy_uninstall_failed")
            self.report["legacyUninstall"]["service"] = self.assert_no_service()
            require(not self.app.exists() and not self.core.exists(), "legacy_uninstall_left_main_or_core")
            require(self.helper.is_file() and digest(self.helper) == legacy_helper_hash, "legacy_helper_not_retained")
            wait_until(lambda: not self.processes.at_paths([self.app, self.core, self.helper]), "legacy_process_survived_uninstall", 15)
            require(registration() is None, "legacy_uninstall_registration_remains")
            self.assert_marker()
            self.report["legacyUninstall"].update({"mainRemoved": True, "coreRemoved": True,
                                                   "helperRetained": True, "configurationPreserved": True})
            # Match the Tauri parent after its synchronous _?= uninstaller exits.
            uninstaller.unlink(missing_ok=True)
            with contextlib.suppress(OSError):
                self.install_dir.rmdir()  # Never recursively delete a residual file.
            self.mark("uninstall_retained_locked_helper")
        finally:
            self.file_lock.Close()
            self.file_lock = None

        self.stage = "legacy_partial_directory_failure"
        result = self.command("legacy_residual_stop", self.helper, ["--stop-for-update"], 65)
        self.report["legacyFailure"] = {"exitCode": result["exitCode"], "stderr": result["stderr"],
                                         "missingFiles": [path.name for path in (self.app, self.core) if not path.exists()]}
        require(result["exitCode"] != 0, "legacy_residual_helper_did_not_reproduce_failure")
        if result["stderr"].strip():
            require("service_binary_missing" in result["stderr"], "legacy_residual_helper_failed_for_other_reason")
        require(self.report["legacyFailure"]["missingFiles"] == [self.app.name, self.core.name], "legacy_missing_files_changed")
        self.mark("published_old_helper_failure_reproduced")

        self.stage = "candidate_repairs_partial_installation"
        result = self.command("candidate_install", self.candidate, ["/S"], 180)
        require(result["exitCode"] == 0, "candidate_install_failed")
        self.assert_candidate_files()
        self.report["candidate"]["serviceAfterRepair"] = self.assert_no_service()
        self.mark("candidate_repairs_uninstall_residue")

        self.stage = "candidate_overwrite_running_service"
        before = self.install_service("candidate_service_install")
        owner_before = self.owner_file.read_bytes()
        # Silent mode skips Tauri's reinstall-choice page and executes the same
        # Install section, including the real PREINSTALL and POSTINSTALL hooks.
        result = self.command("candidate_overwrite", self.candidate, ["/S"], 180)
        require(result["exitCode"] == 0, "candidate_overwrite_failed")
        after = self.running_service()
        require(after != before, "running_service_process_was_not_replaced")
        require(self.owner_file.read_bytes() == owner_before, "overwrite_changed_service_owner")
        require(owned_firewall_rule_count(self.core) == 1, "overwrite_lost_firewall_rule")
        self.assert_candidate_files()
        self.report["runningServiceOverwrite"] = {"pidBefore": before.pid, "pidAfter": after.pid,
            "createdBefore": before.created, "createdAfter": after.created,
            "processReplaced": True, "ownerSidPreserved": True, "configurationPreserved": True}
        self.mark("overwrite_restarts_existing_service")

        self.stage = "candidate_application_uninstall"
        running = self.running_service()
        uninstaller = self.install_dir / "uninstall.exe"
        raw = uninstall_command(uninstaller)
        self.report["candidateUninstall"] = {"commandLine": raw, "servicePidBefore": running.pid}
        result = self.command("candidate_uninstall", uninstaller, raw, 180)
        remaining = {name: (self.install_dir / name).exists() for name in EXECUTABLES}
        self.report["candidateUninstall"]["remainingFiles"] = remaining
        self.write_report()
        require(result["exitCode"] == 0, "candidate_uninstall_failed")
        self.report["candidateUninstall"]["service"] = self.assert_no_service()
        require(not any(remaining.values()), "candidate_uninstall_left_program_files")
        require(not self.owner_file.exists(), "uninstalled_service_owner_remains")
        require(registration() is None, "candidate_uninstall_registration_remains")
        self.assert_marker()
        wait_until(lambda: not self.processes.at_paths([self.app, self.core, self.helper]), "upgrade_process_remains", 15)
        self.report["candidateUninstall"].update({"ownerRemoved": True, "registrationRemoved": True,
                                                   "configurationPreserved": True})
        # Only the in-place uninstaller needs parent cleanup. The assertions
        # above require NSIS itself to have removed all four installed programs.
        uninstaller.unlink(missing_ok=True)
        with contextlib.suppress(OSError):
            self.install_dir.rmdir()
        self.mark("candidate_application_fully_uninstalled")

        self.stage = "candidate_reinstall_for_gui"
        require(self.command("candidate_reinstall", self.candidate, ["/S"], 180)["exitCode"] == 0,
                "candidate_reinstall_failed")
        self.assert_candidate_files()
        self.report["candidate"]["serviceAfterReinstall"] = self.assert_no_service()
        require(not self.owner_file.exists(), "candidate_reinstall_created_service_owner")
        wait_until(lambda: not self.processes.at_paths([self.app, self.core, self.helper]), "reinstall_started_process", 15)
        self.mark("candidate_reinstalled_for_gui")

    def remove_owned_service(self) -> None:
        """Failure cleanup is restricted to the service this test installed."""
        import pywintypes
        import win32con
        import win32service

        snapshot = service_snapshot()
        if snapshot.get("winError") == 1060:
            return
        require(executable_path(snapshot.get("binary", "")) == os.path.normcase(str(self.helper.resolve())),
                "cleanup_service_identity_mismatch")
        manager = win32service.OpenSCManager(None, None, win32service.SC_MANAGER_CONNECT)
        try:
            service = win32service.OpenService(manager, SERVICE_NAME,
                win32service.SERVICE_STOP | win32service.SERVICE_QUERY_STATUS | win32con.DELETE)
            try:
                def stopped():
                    status = win32service.QueryServiceStatusEx(service)
                    if status["CurrentState"] == win32service.SERVICE_STOPPED:
                        return True
                    try:
                        win32service.ControlService(service, win32service.SERVICE_CONTROL_STOP)
                    except pywintypes.error as error:
                        if error.winerror not in (1061, 1062):
                            raise
                    return False
                wait_until(stopped, "cleanup_service_stop_timeout", 45)
                win32service.DeleteService(service)
            finally:
                win32service.CloseServiceHandle(service)
        finally:
            win32service.CloseServiceHandle(manager)
        wait_until(lambda: service_snapshot().get("winError") == 1060, "cleanup_service_delete_timeout", 15)

    def remove_owned_firewall_rule(self) -> None:
        import pythoncom
        from win32com.client import dynamic

        # Readback verifies exact rule name + installation path + group before
        # deleting a rule created by this test. No firewall profile is changed.
        rule = owned_firewall_rule(self.core)
        if rule is None:
            return
        policy = None
        pythoncom.CoInitializeEx(pythoncom.COINIT_APARTMENTTHREADED)
        try:
            policy = dynamic.Dispatch("HNetCfg.FwPolicy2")
            policy.Rules.Remove(rule["name"])
        finally:
            policy = None
            pythoncom.CoUninitialize()

    def cleanup(self) -> None:
        if not self.mutations_started:
            return
        failures = []
        def attempt(name, operation):
            try:
                operation()
            except Exception as error:
                failures.append({"stage": "cleanup", "code": name, "detail": failure_code(error)})
        if self.file_lock is not None:
            attempt("file_lock_close", self.file_lock.Close)
            self.file_lock = None
        for child in list(self.active_children):
            attempt("exact_child_stop", lambda child=child: self.stop_child(child))
        if self.service_owned:
            attempt("owned_service_remove", self.remove_owned_service)
            attempt("owned_firewall_remove", self.remove_owned_firewall_rule)
            attempt("service_absence_check", self.assert_no_service)
        attempt("process_absence_check", lambda: require(
            not self.processes.at_paths([self.app, self.core, self.helper]), "owned_process_remains"))
        if self.marker_hash:
            # Preserve this owned marker for the following GUI test to snapshot
            # and restore. Never remove or replace a preexisting user config.
            attempt("configuration_preservation_check", self.assert_marker)
        self.report["diagnostics"].extend(failures)
        self.report["tests"]["final_cleanup"] = not failures
        self.write_report()


def main() -> int:
    artifacts = Path("artifacts/verification")
    acceptance = None
    allowed = sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
    try:
        require(allowed, "disposable_github_windows_runner_required")
        artifacts.mkdir(parents=True, exist_ok=True)
        require(ctypes.windll.shell32.IsUserAnAdmin(), "elevated_runner_required_for_upgrade_acceptance")
        acceptance = UpgradeAcceptance(artifacts)
        acceptance.write_report()
        acceptance.run()
    except Exception as error:
        code = failure_code(error)
        print("FAIL: upgrade_" + code, file=sys.stderr, flush=True)
        if acceptance:
            acceptance.report["diagnostics"].append({"stage": acceptance.stage, "code": code})
        elif allowed:
            (artifacts / "upgrade.json").write_text(json.dumps({"passed": False, "tests": {},
                "diagnostics": [{"stage": "preflight", "code": code}]}, indent=2) + "\n", encoding="utf-8")
    finally:
        if acceptance:
            try:
                acceptance.cleanup()
            except Exception as error:
                acceptance.report["diagnostics"].append({"stage": "cleanup", "code": failure_code(error)})
            required = {"published_baseline_verified", "legacy_service_running", "uninstall_retained_locked_helper",
                        "published_old_helper_failure_reproduced", "candidate_repairs_uninstall_residue",
                        "overwrite_restarts_existing_service", "candidate_application_fully_uninstalled",
                        "candidate_reinstalled_for_gui", "final_cleanup"}
            acceptance.report["passed"] = (all(acceptance.report["tests"].get(name) is True for name in required)
                                           and not acceptance.report["diagnostics"])
            acceptance.write_report()
    return 0 if acceptance and acceptance.report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
