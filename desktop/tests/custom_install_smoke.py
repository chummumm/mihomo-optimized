#!/usr/bin/env python3
"""Real NSIS/TUN acceptance outside Program Files, including a separate volume.

Only disposable, elevated GitHub Windows runners may execute this fixture.
The second disk is a newly created 1 GiB expandable VHD; no existing disk is
formatted and no shared parent directory has its permissions changed. The
normal upgrade/UI suite runs afterwards against its unchanged default path.
"""

from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path
import shutil
import stat
import sys
import tempfile
import time
import uuid
import zipfile

from upgrade_smoke import (
    EXECUTABLES, PRODUCT, UNINSTALL_KEY, UpgradeAcceptance, digest, executable_path,
    failure_code, registration, service_snapshot, uninstall_command,
)
from upgrade_tun_smoke import TunUpgradeAcceptance
from core_update_smoke import CoreUpdateAcceptance
from windows_smoke import RestrictedDesktop, SERVICE_NAME, SocksFixture, require, wait_until


BROKEN_VERSION = "0.2.13"
BROKEN_SOURCE = "dae8d7c987d1b04633184fe9a243a457edc412a0"
BROKEN_URL = (
    "https://github.com/chummumm/mihomo-optimized/releases/download/"
    "mihomo-desktop-v0.2.13/mihomo-desktop-0.2.13-windows-x64-setup.exe"
)
BROKEN_SHA256 = "6429ffbde55e989fd41246075ae281e9bcdcf5d40db242b7804660f1fc831a07"
SAFE_DIRECTORY_SDDL = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)"
UNSAFE_DIRECTORY_SDDL = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)"
REQUIRED_CASES = {
    "default_never_service_uninstall", "same_volume_tun", "separate_volume_unicode_tun",
    "broken_0_2_13_reproduced", "broken_0_2_13_repaired", "missing_runtime_uninstall",
    "unsafe_parent_rejected_before_copy", "reparse_parent_rejected_before_copy",
    "failed_upgrade_preserves_running_install", "unsafe_install_can_uninstall",
    "foreign_service_preserved", "custom_core_update", "custom_desktop_update_identity",
    "broken_install_remove_mode", "retained_directory_authority_rejected",
    "unsafe_directory_removed_and_recreated", "empty_unsafe_directory_recreated",
    "legacy_registry_handle_retired", "weak_registry_foreign_target_rejected", "final_cleanup",
}


def set_directory_security(path: Path, sddl: str = SAFE_DIRECTORY_SDDL) -> None:
    import win32security

    require(path.is_dir() and not path.is_symlink(), "fixture_security_target_invalid")
    descriptor = win32security.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1)
    win32security.SetFileSecurity(str(path), win32security.OWNER_SECURITY_INFORMATION
        | win32security.DACL_SECURITY_INFORMATION | win32security.PROTECTED_DACL_SECURITY_INFORMATION,
        descriptor)


def remove_owned_tree(path: Path) -> None:
    """Remove only a root claimed absent before this fixture, without following links."""
    try:
        metadata = path.lstat()
    except FileNotFoundError:
        return
    require(not metadata.st_file_attributes & 0x400, "fixture_cleanup_reparse_point")
    if stat.S_ISDIR(metadata.st_mode):
        for child in path.iterdir():
            remove_owned_tree(child)
        path.rmdir()
    else:
        require(stat.S_ISREG(metadata.st_mode) and metadata.st_nlink == 1,
                "fixture_cleanup_unexpected_file")
        path.unlink()


def volume_identity(root: Path) -> dict:
    import win32api
    import win32file

    name = ctypes.create_unicode_buffer(128)
    require(ctypes.windll.kernel32.GetVolumeNameForVolumeMountPointW(
        ctypes.c_wchar_p(str(root)), name, len(name)), "fixture_volume_identity_unavailable")
    _label, serial, _maximum, flags, filesystem = win32api.GetVolumeInformation(str(root))
    return {"guid": name.value, "serial": serial, "filesystem": filesystem,
            "persistentAcls": bool(flags & 0x8), "driveType": win32file.GetDriveType(str(root))}


def directory_identity(path: Path) -> tuple[int, int, int]:
    import win32file

    handle = win32file.CreateFile(str(path), 0x80, 7, None, 3, 0x02000000 | 0x00200000, None)
    try:
        information = win32file.GetFileInformationByHandle(handle)
        require(information[0] & 0x10 and not information[0] & 0x400, "fixture_directory_handle_invalid")
        return information[4], information[8], information[9]
    finally:
        handle.Close()


class RetainedDirectoryAuthority:
    """An ordinary user's already granted WRITE_DAC survives later ACL changes."""
    def __init__(self, path: Path) -> None:
        import win32file
        import win32security

        self.desktop = RestrictedDesktop()
        self.handle = None
        try:
            win32security.ImpersonateLoggedOnUser(self.desktop.token)
            try:
                # No data-write or DELETE access: denying write/delete sharing
                # alone cannot exclude this retained permission-changing handle.
                self.handle = win32file.CreateFile(str(path), 0x40000 | 0x20000 | 0x80,
                                                  7, None, 3, 0x02000000 | 0x00200000, None)
            finally:
                win32security.RevertToSelf()
            information = win32file.GetFileInformationByHandle(self.handle)
            self.identity = information[4], information[8], information[9]
            require(self.reset_old_acl() == 0, "fixture_retained_write_dac_not_effective")
        except BaseException:
            self.close()
            raise

    def reset_old_acl(self) -> int:
        import pywintypes
        import win32security

        descriptor = win32security.ConvertStringSecurityDescriptorToSecurityDescriptor(UNSAFE_DIRECTORY_SDDL, 1)
        win32security.ImpersonateLoggedOnUser(self.desktop.token)
        try:
            win32security.SetSecurityInfo(self.handle, win32security.SE_FILE_OBJECT,
                win32security.DACL_SECURITY_INFORMATION | win32security.PROTECTED_DACL_SECURITY_INFORMATION,
                None, None, descriptor.GetSecurityDescriptorDacl(), None)
            return 0
        except pywintypes.error as error:
            return error.winerror
        finally:
            win32security.RevertToSelf()

    def assert_fresh_boundary(self, target: Path, evidence: dict) -> None:
        import pywintypes
        import win32file
        import win32security

        actual = directory_identity(target)
        require(actual != self.identity, "unsafe_directory_object_reused")
        evidence.update({"oldDirectoryIdentity": self.identity, "newDirectoryIdentity": actual})
        if self.handle is not None:
            # A successful old-handle write may affect only the detached object.
            evidence["oldHandleAclResetWinError"] = self.reset_old_acl()
        denied = []
        win32security.ImpersonateLoggedOnUser(self.desktop.token)
        try:
            for name, access in (("WRITE_DAC", 0x40000), ("FILE_ADD_FILE", 0x2), ("FILE_DELETE_CHILD", 0x40)):
                try:
                    handle = win32file.CreateFile(str(target), access, 7, None, 3, 0x02000000, None)
                except pywintypes.error as error:
                    require(error.winerror == 5, "fresh_directory_access_check_failed")
                    denied.append(name)
                else:
                    handle.Close()
                    require(False, "fresh_directory_allows_ordinary_" + name.lower())
        finally:
            win32security.RevertToSelf()
        evidence["ordinaryAccessDenied"] = denied

    def release_handle(self) -> None:
        if self.handle is not None:
            self.handle.Close()
            self.handle = None

    def close(self) -> None:
        self.release_handle()
        self.desktop.close()


class RetainedRegistryAuthority:
    """Grant only this fixture's old product key an ordinary user's retained write."""
    def __init__(self) -> None:
        import win32security
        import winreg

        self.desktop = RestrictedDesktop()
        self.handle = None
        self.information = win32security.DACL_SECURITY_INFORMATION
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, UNINSTALL_KEY, 0,
                                winreg.KEY_READ | 0x40000 | winreg.KEY_WOW64_64KEY) as key:
                self.original_location = winreg.QueryValueEx(key, "InstallLocation")[0]
                descriptor = win32security.GetSecurityInfo(int(key), win32security.SE_REGISTRY_KEY, self.information)
                self.original_sddl = win32security.ConvertSecurityDescriptorToStringSecurityDescriptor(
                    descriptor, 1, self.information)
                self.original_protected = bool(descriptor.GetSecurityDescriptorControl()[0] & 0x1000)
                dacl = descriptor.GetSecurityDescriptorDacl()
                dacl.AddAccessAllowedAceEx(dacl.GetAclRevision(), 0, winreg.KEY_SET_VALUE,
                    win32security.CreateWellKnownSid(win32security.WinBuiltinUsersSid, None))
                win32security.SetSecurityInfo(int(key), win32security.SE_REGISTRY_KEY, self.information,
                                             None, None, dacl, None)
            win32security.ImpersonateLoggedOnUser(self.desktop.token)
            try:
                self.handle = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, UNINSTALL_KEY, 0,
                                            winreg.KEY_SET_VALUE | winreg.KEY_WOW64_64KEY)
            finally:
                win32security.RevertToSelf()
            require(self.write_location(self.original_location) == 0, "fixture_legacy_registry_write_unavailable")
        except BaseException:
            self.close()
            raise

    def write_location(self, value: str) -> int:
        import win32security
        import winreg

        win32security.ImpersonateLoggedOnUser(self.desktop.token)
        try:
            winreg.SetValueEx(self.handle, "InstallLocation", 0, winreg.REG_SZ, value)
            return 0
        except OSError as error:
            return error.winerror
        finally:
            win32security.RevertToSelf()

    def assert_retired(self, target: Path) -> dict:
        import win32security
        import winreg

        before = registration()
        error = self.write_location(str(target.parent / "stale registry handle target"))
        require(before is not None and registration() == before, "old_registry_handle_changed_new_installation")
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, UNINSTALL_KEY, 0,
                            winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
            descriptor = win32security.GetSecurityInfo(int(key), win32security.SE_REGISTRY_KEY, self.information)
            require(descriptor.GetSecurityDescriptorControl()[0] & 0x1000, "new_registry_dacl_still_inherits")
            dacl = descriptor.GetSecurityDescriptorDacl()
            forbidden = 0x40000000 | 0x10000000 | 0x10000 | 0x40000 | 0x80000 | 0x2 | 0x4 | 0x20
            for index in range(dacl.GetAceCount()):
                ace = dacl.GetAce(index)
                if ace[0][1] & 0x8 or ace[0][0] in (1, 6, 10, 12):
                    continue
                require(win32security.ConvertSidToStringSid(ace[-1]) != "S-1-5-13" or not ace[1] & forbidden,
                        "new_registry_allows_terminal_server_user_write")
            sddl = win32security.ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, self.information)
        win32security.ImpersonateLoggedOnUser(self.desktop.token)
        try:
            try:
                key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, UNINSTALL_KEY, 0,
                                    winreg.KEY_SET_VALUE | winreg.KEY_WOW64_64KEY)
            except OSError as denied:
                require(denied.winerror == 5, "new_registry_access_check_failed")
            else:
                key.Close()
                require(False, "new_registry_allows_ordinary_write")
        finally:
            win32security.RevertToSelf()
        return {"retiredHandleWriteWinError": error, "newKeyOrdinaryWriteDenied": True, "newKeyDacl": sddl}

    def restore(self) -> None:
        import win32security
        import winreg

        require(self.write_location(self.original_location) == 0, "fixture_legacy_registry_restore_failed")
        descriptor = win32security.ConvertStringSecurityDescriptorToSecurityDescriptor(self.original_sddl, 1)
        protection = (win32security.PROTECTED_DACL_SECURITY_INFORMATION if self.original_protected
                      else win32security.UNPROTECTED_DACL_SECURITY_INFORMATION)
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, UNINSTALL_KEY, 0, 0x40000 | winreg.KEY_WOW64_64KEY) as key:
            win32security.SetSecurityInfo(int(key), win32security.SE_REGISTRY_KEY, self.information | protection,
                                         None, None, descriptor.GetSecurityDescriptorDacl(), None)

    def close(self) -> None:
        if self.handle is not None:
            self.handle.Close()
            self.handle = None
        self.desktop.close()


class DisposableVolume:
    def __init__(self, owner: "CustomInstallAcceptance") -> None:
        self.owner = owner
        self.temp = tempfile.TemporaryDirectory(prefix="mihomo-install-volume-")
        self.directory = Path(self.temp.name).resolve(strict=True)
        set_directory_security(self.directory)
        self.image = self.directory / "installation.vhd"
        self.root = None
        self.attached = False

    def diskpart(self, label: str, commands: list[str]) -> None:
        require('"' not in str(self.image) and not self.image.is_symlink(), "fixture_vhd_path_invalid")
        script = self.directory / (label + ".txt")
        script.write_text("\n".join(commands + ["exit"]) + "\n", encoding="utf-8")
        tool = Path(os.environ["SYSTEMROOT"]) / "System32/diskpart.exe"
        result = self.owner.command(label, tool, ["/s", str(script)], 90)
        require(result["exitCode"] == 0, label + "_failed")

    def create(self) -> Path:
        mask = ctypes.windll.kernel32.GetLogicalDrives()
        require(mask != 0, "fixture_drive_inventory_failed")
        available = [letter for letter in "VUTSRQPONMLKJIHGFE" if not mask & (1 << (ord(letter) - ord("A")))]
        require(available, "fixture_drive_letter_unavailable")
        letter = available[0]
        self.root = Path(letter + ":\\")
        require(not self.image.exists() and not self.root.exists(), "fixture_volume_already_exists")
        # DiskPart stops its script on errors (no `noerr` anywhere). Every disk
        # operation follows selection of this newly created, uniquely named VHD.
        self.attached = True  # Also detach if creation fails after attachment.
        self.diskpart("create_fixture_volume", [
            f'create vdisk file="{self.image}" maximum=1024 type=expandable',
            f'select vdisk file="{self.image}"', "attach vdisk", "create partition primary",
            "format fs=ntfs quick label=MihomoPathTest", "assign letter=" + letter,
        ])
        wait_until(self.root.exists, "fixture_volume_not_mounted", 15)
        actual = volume_identity(self.root)
        system = volume_identity(Path(os.environ["SYSTEMDRIVE"] + "\\"))
        require(actual["driveType"] == 3 and actual["persistentAcls"]
                and actual["filesystem"] == "NTFS" and actual["guid"] != system["guid"],
                "fixture_not_a_separate_local_acl_volume")
        self.owner.report["secondVolume"] = {"kind": "expandable_vhd", "maximumMiB": 1024,
                                             "root": str(self.root), **actual}
        # This whole volume belongs to the fixture; never tighten a shared D:.
        set_directory_security(self.root)
        return self.root

    def close(self) -> None:
        if self.attached and self.image.exists():
            self.diskpart("detach_fixture_volume", [f'select vdisk file="{self.image}"', "detach vdisk"])
            if self.root is not None:
                wait_until(lambda: not self.root.exists(), "fixture_volume_still_attached", 15)
            self.attached = False
        self.temp.cleanup()


class CustomInstallAcceptance(UpgradeAcceptance):
    def __init__(self, artifacts: Path) -> None:
        super().__init__(artifacts)
        self.report = {"passed": False, "tests": {}, "commands": [], "diagnostics": [], "paths": []}
        self.default_directory = self.install_dir
        self.fixture_root = Path(os.environ["SYSTEMDRIVE"] + "\\") / ("MihomoPathTest-" + uuid.uuid4().hex)
        self.installed_paths: list[Path] = []
        self.owned_data: list[Path] = []
        self.volume: DisposableVolume | None = None
        self.tun = None
        self.redirect: Path | None = None
        self.foreign_helper: Path | None = None
        self.diagnostic_directory: Path | None = None

    def write_report(self) -> None:
        temporary = self.artifacts / "custom-install.json.tmp"
        temporary.write_text(json.dumps(self.report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        temporary.replace(self.artifacts / "custom-install.json")

    def select(self, directory: Path) -> None:
        self.install_dir = directory
        self.app, self.core, self.helper, self.verifier, self.host = [directory / name for name in EXECUTABLES]

    def command(self, name: str, executable: Path, arguments: list[str] | str,
                timeout: float = 90) -> dict:
        installer = executable.name.endswith("-setup.exe") or executable.name == "uninstall.exe"
        target = self.install_dir
        if isinstance(arguments, str) and " /D=" in arguments:
            target = Path(arguments.rsplit(" /D=", 1)[1])
        try:
            result = super().command(name, executable, arguments, timeout)
        except Exception:
            if installer:
                self.capture_installer_failure(name, target)
            raise
        if installer and result["exitCode"] != 0:
            self.capture_installer_failure(name, target)
        return result

    def diagnostic_helper(self) -> Path:
        """Execute only the verified candidate bytes from a separate owned ACL boundary."""
        import win32security

        if self.diagnostic_directory is None:
            directory = Path(os.environ["ProgramFiles"]) / ("MihomoDiagnostics-" + uuid.uuid4().hex)
            directory.mkdir()  # Never reuse a pre-existing maintenance directory.
            self.diagnostic_directory = directory
            set_directory_security(directory)
            helper = directory / "mihomo-maintenance.exe"
            archive = self.candidate.with_name(self.candidate.name.removesuffix("-setup.exe") + "-portable.zip")
            with zipfile.ZipFile(archive) as bundle:
                with bundle.open(PRODUCT + "/mihomo-desktop-service.exe") as source, helper.open("xb") as output:
                    shutil.copyfileobj(source, output)
            require(digest(helper) == self.expected_hashes["mihomo-desktop-service.exe"],
                    "diagnostic_candidate_helper_hash_mismatch")
            manifest = directory / "installer-files.json"
            payload = Path("desktop/generated/installer-files.json").read_bytes()
            require(len(payload) < 1024 * 1024, "diagnostic_manifest_too_large")
            with manifest.open("xb") as output:
                output.write(payload)
            descriptor = win32security.ConvertStringSecurityDescriptorToSecurityDescriptor(
                "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)", 1)
            for path in (helper, manifest):
                win32security.SetFileSecurity(str(path), win32security.OWNER_SECURITY_INFORMATION
                    | win32security.DACL_SECURITY_INFORMATION | win32security.PROTECTED_DACL_SECURITY_INFORMATION,
                    descriptor)
        helper = self.diagnostic_directory / "mihomo-maintenance.exe"
        require(digest(helper) == self.expected_hashes["mihomo-desktop-service.exe"],
                "diagnostic_candidate_helper_hash_mismatch")
        return helper

    def capture_installer_failure(self, name: str, target: Path) -> None:
        """Read-only evidence supplements a failure; it never changes its assertion."""
        import win32security
        import winreg

        event = {"command": name, "target": str(target), "paths": [], "registry": [], "probes": []}
        self.report.setdefault("installerFailureEvidence", []).append(event)
        information = win32security.OWNER_SECURITY_INFORMATION | win32security.DACL_SECURITY_INFORMATION
        try:
            critical = {*EXECUTABLES, "uninstall.exe", "wintun.dll", "resources/build-info.json"}
            critical.update(path for path in self.expected_hashes
                            if path.endswith((".deps.json", ".runtimeconfig.json")))
            directories = {parent for path in self.expected_hashes for parent in Path(path).parents}
            paths = {target, *target.parents, *(target / path for path in critical | directories)}
            for path in sorted(paths, key=str):
                row = {"path": str(path)}
                event["paths"].append(row)
                try:
                    metadata = path.lstat()
                    row.update({"attributes": metadata.st_file_attributes, "links": metadata.st_nlink,
                                "isDirectory": stat.S_ISDIR(metadata.st_mode)})
                    descriptor = win32security.GetNamedSecurityInfo(str(path), win32security.SE_FILE_OBJECT, information)
                    row["sddl"] = win32security.ConvertSecurityDescriptorToStringSecurityDescriptor(
                        descriptor, 1, information)
                except Exception as error:
                    row["error"] = failure_code(error)
            for view, flag in ((64, winreg.KEY_WOW64_64KEY), (32, winreg.KEY_WOW64_32KEY)):
                row = {"view": view}
                event["registry"].append(row)
                try:
                    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, UNINSTALL_KEY, 0, winreg.KEY_READ | flag) as key:
                        descriptor = win32security.GetSecurityInfo(int(key), win32security.SE_REGISTRY_KEY, information)
                        row["sddl"] = win32security.ConvertSecurityDescriptorToStringSecurityDescriptor(
                            descriptor, 1, information)
                        row["values"] = {}
                        for value in ("DisplayVersion", "InstallLocation", "MainBinaryName", "UninstallString"):
                            try:
                                row["values"][value] = winreg.QueryValueEx(key, value)[0]
                            except OSError as error:
                                row["values"][value] = {"error": failure_code(error)}
                except Exception as error:
                    row["error"] = failure_code(error)
            helper = self.diagnostic_helper()
            for probe, arguments in (
                ("installer_preflight", ["--installer-check", str(target), str(helper.parent / "installer-files.json")]),
                ("validate_directory", ["--validate-directory", str(target)]),
            ):
                result = super().command(name + "_diagnostic_" + probe, helper, arguments, 45)
                event["probes"].append({"probe": probe, "exitCode": result["exitCode"],
                                        "stdout": result["stdout"], "stderr": result["stderr"]})
        except Exception as error:
            event["captureError"] = failure_code(error)
        self.write_report()
        # NSIS is a GUI executable and may emit nothing on its own stdout. Keep
        # these explicitly labelled probes visible even on the first failed run.
        print("INSTALLER_FAILURE_EVIDENCE " + json.dumps({key: value for key, value in event.items()
              if key not in ("paths", "registry")}, ensure_ascii=True), flush=True)

    def install_at(self, directory: Path, label: str, *, installer: Path | None = None) -> None:
        require(registration() is None and registration(32) is None, "previous_fixture_installation_remains")
        require(service_snapshot().get("winError") == 1060, "previous_fixture_service_remains")
        self.select(directory)
        require(not directory.exists(), "fixture_install_directory_preexists")
        self.installed_paths.append(directory)
        self.stage = label
        executable = installer or self.candidate
        # NSIS /D= must be the final, unquoted argument, including spaces.
        command = f'"{executable}" /S /D={directory}'
        result = self.command(label, executable, command, 180)
        require(result["exitCode"] == 0, label + "_failed")
        self.assert_registration(BROKEN_VERSION if installer else self.info["desktopVersion"])
        self.report["paths"].append({"label": label, "path": str(directory)})
        if installer is None:
            self.assert_candidate_files()

    def uninstall_current(self, label: str, *, through_setup: bool = False) -> None:
        self.stage = label
        uninstaller = self.install_dir / "uninstall.exe"
        require(uninstaller.is_file(), "fixture_uninstaller_missing")
        result = (self.command(label, self.candidate, ["/S", "/REMOVE"], 180) if through_setup
                  else self.command(label, uninstaller, uninstall_command(uninstaller), 180))
        require(result["exitCode"] == 0, label + "_failed")
        self.assert_no_service()
        require(registration() is None and registration(32) is None, "fixture_uninstall_record_remains")
        require(not self.owner_file.exists(), "fixture_uninstall_service_owner_remains")
        require(not self.processes.at_paths([self.app, self.core, self.helper, self.host]),
                "fixture_uninstall_process_remains")
        require(not any((self.install_dir / name).exists() for name in self.expected_hashes),
                "fixture_uninstall_runtime_remains")
        self.assert_marker()
        # _?= keeps the exact uninstaller synchronous and leaves its own image.
        uninstaller.unlink(missing_ok=True)
        if self.install_dir.exists():
            self.install_dir.rmdir()
        self.service_owned = False

    def tun_lifecycle(self, label: str, *, check_desktop_update: bool = False) -> None:
        self.stage = label
        self.install_service(label + "_service")
        tun = self.tun = TunUpgradeAcceptance(self, self.candidate, self.info["desktopVersion"], self.info["uiCommit"])
        self.report.setdefault("tunByPath", {})[label] = tun.evidence
        try:
            tun.desktop = RestrictedDesktop()
            require(tun.desktop.sid == self.sid, "custom_gui_owner_changed")
            tun.socks = SocksFixture()
            name = "MihomoPathTun"
            tun.native.require_unused(name)
            tun.configure(tun.source(name))
            tun.launch_gui(label)
            if check_desktop_update:
                info = tun.driver.invoke("desktop_check_update", timeout=60)
                require(info.get("canInstall") is True, "custom_desktop_update_not_installable")
                self.mark("custom_desktop_update_identity")
            evidence = tun.evidence["scenarios"]["customPath"] = {}
            tun.start_gui(evidence, "initial")
            status, previous = tun.running(name)
            tun.capture(name, True, previous)
            tun.forwarding(evidence, "initial")
            for cycle in range(2):
                deadline = time.monotonic() + 60
                record = {}
                tun.driver.invoke("desktop_restart", timeout=60)
                wait_until(lambda: tun.restarted_health(previous, status["intentRevision"], deadline, record),
                           "custom_tun_restart_not_ready", tun.remaining(deadline))
                status, current = tun.running(name)
                record.update(tun.confirm_process_handoff(previous, current))
                tun.capture(name, True, current)
                tun.forwarding(record, "restarted")
                evidence.setdefault("restarts", []).append(record)
                previous = current
            tun.close_gui()
            tun.unchanged_source(evidence, "after_stop")
            # Restore the source marker before exercising actual product cleanup.
            self.config.write_bytes(tun.original_config)
            self.running_service()
            self.uninstall_current(label + "_uninstall")
            self.mark(label)
        finally:
            tun.cleanup()
            self.tun = None

    def reproduce_and_repair(self, target: Path) -> None:
        legacy = self.download_baseline(BROKEN_VERSION, BROKEN_URL, BROKEN_SHA256, 128 * 1024 * 1024)
        self.install_at(target, "broken_custom_install", installer=legacy)
        installed = json.loads((target / "resources/build-info.json").read_text(encoding="utf-8-sig"))
        require(installed.get("uiCommit") == BROKEN_SOURCE, "broken_baseline_source_mismatch")
        result = self.command("broken_tun_service", self.helper, ["--install", "--owner-sid", self.sid])
        require(result["exitCode"] != 0 and "service_install_required" in result["stderr"],
                "published_custom_tun_failure_not_reproduced")
        uninstaller = target / "uninstall.exe"
        result = self.command("broken_custom_uninstall", uninstaller, uninstall_command(uninstaller), 180)
        require(result["exitCode"] != 0 and registration() is not None and self.app.is_file(),
                "published_custom_uninstall_failure_not_reproduced")
        self.mark("broken_0_2_13_reproduced")
        authority = RetainedRegistryAuthority()
        try:
            self.stage = "repair_broken_custom_install"
            require(self.command(self.stage, self.candidate, ["/S"], 180)["exitCode"] == 0,
                    "broken_custom_repair_failed")
            self.assert_registration(self.info["desktopVersion"])
            self.assert_candidate_files()
            self.report["legacyRegistryReplacement"] = authority.assert_retired(target)
            self.mark("legacy_registry_handle_retired")
        finally:
            authority.close()
        self.tun_lifecycle("broken_0_2_13_repaired")

    def missing_runtime(self, target: Path) -> None:
        self.install_at(target, "partial_custom_install")
        self.install_service("partial_service")
        require(self.command("partial_stop", self.helper, ["--stop-for-update"])["exitCode"] == 0,
                "partial_service_stop_failed")
        wait_until(lambda: service_snapshot().get("state") == 1, "partial_service_not_stopped", 30)
        for path in (self.helper, self.core, self.host):
            path.unlink()
        require(service_snapshot().get("installed") is True, "partial_scm_fixture_missing")
        self.uninstall_current("partial_custom_uninstall")
        self.mark("missing_runtime_uninstall")

    def custom_core_update(self, target: Path) -> None:
        self.install_at(target, "custom_core_update_install")
        artifacts = self.artifacts / "custom-path-core-update"
        artifacts.mkdir()
        fixture = CoreUpdateAcceptance(self.app, artifacts)
        fixture.report_path = artifacts / "core-update.json"
        try:
            fixture.run(scenarios=("tun_auto_update",), check_legacy=False)
        finally:
            fixture.cleanup()
            fixture.checkpoint()
            self.report["customCoreUpdate"] = fixture.report
            self.write_report()
        require(all(fixture.report["tests"].get(name) is True for name in
                    ("tun_auto_update", "desktop_application_unchanged", "original_bundle_restored"))
                and not fixture.report["diagnostics"], "custom_path_core_update_failed")
        self.assert_candidate_files()
        self.uninstall_current("custom_core_update_uninstall")
        self.mark("custom_core_update")

    def remove_broken_install(self, target: Path) -> None:
        legacy = self.download_baseline(BROKEN_VERSION, BROKEN_URL, BROKEN_SHA256, 128 * 1024 * 1024)
        self.install_at(target, "broken_remove_install", installer=legacy)
        foreign = target.parent / "非本产品 注册目标"
        foreign.mkdir()
        set_directory_security(foreign)
        for name in (*EXECUTABLES, "uninstall.exe"):
            (foreign / name).write_bytes(b"fixture sentinel; never execute\n")
        protected_files = {path: digest(path) for directory in (target, foreign)
                           for path in directory.rglob("*") if path.is_file()}
        authority = RetainedRegistryAuthority()
        try:
            require(authority.write_location(str(foreign)) == 0, "fixture_weak_registration_not_writable")
            before = registration()
            self.stage = "weak_registry_foreign_target_rejected"
            self.select(foreign)
            try:
                result = self.command(self.stage, self.candidate, ["/S", "/REMOVE"], 180)
            finally:
                self.select(target)
            require(result["exitCode"] != 0 and registration() == before,
                    "weak_registry_redirected_product_removal")
            require(all(path.is_file() and digest(path) == checksum for path, checksum in protected_files.items()),
                    "weak_registry_removal_changed_original_or_foreign_files")
            self.report["weakRegistryRedirection"] = {"original": str(target), "foreign": str(foreign),
                "unchangedFiles": len(protected_files), "installerExitCode": result["exitCode"]}
            self.mark(self.stage)
        finally:
            try:
                authority.restore()
            finally:
                authority.close()
        try:
            set_directory_security(target.parent, UNSAFE_DIRECTORY_SDDL)
            # Normal reinstall cannot secure someone else's shared parent. The
            # fresh setup's explicit maintenance route must still remove this
            # old installation without running its broken installed helper.
            self.uninstall_current("broken_remove_setup", through_setup=True)
            self.mark("broken_install_remove_mode")
        finally:
            set_directory_security(target.parent)

    def recreate_with_retained_handle(self, target: Path, authority: RetainedDirectoryAuthority,
                                     label: str, evidence: dict) -> None:
        self.stage = label
        self.select(target)
        command = f'"{self.candidate}" /S /D={target}'
        result = self.command(label, self.candidate, command, 180)
        evidence["firstInstallExitCode"] = result["exitCode"]
        if result["exitCode"] != 0:
            # Classic Windows deletion keeps the old object delete-pending
            # until its last handle closes. Refusal is safe; ACL hardening and
            # accepting that same object would leave a privilege escalation.
            require(registration() is None and registration(32) is None
                    and service_snapshot().get("winError") == 1060,
                    "retained_handle_failure_created_installation")
            evidence["pendingHandleAclResetWinError"] = authority.reset_old_acl()
            authority.release_handle()
            wait_until(lambda: not target.exists(), "old_directory_not_removed_after_handle_close", 10)
            self.install_at(target, label + "_after_handle_close")
            evidence["requiredHandleClose"] = True
        else:
            self.assert_registration(self.info["desktopVersion"])
            self.assert_candidate_files()
            evidence["requiredHandleClose"] = False
        authority.assert_fresh_boundary(target, evidence)
        self.assert_candidate_files()
        self.install_service(label + "_service")
        self.running_service()
        authority.release_handle()
        self.uninstall_current(label + "_uninstall")
        self.mark(label)

    def retained_directory_authority(self, target: Path) -> None:
        legacy = self.download_baseline(BROKEN_VERSION, BROKEN_URL, BROKEN_SHA256, 128 * 1024 * 1024)
        self.install_at(target, "unsafe_legacy_directory_install", installer=legacy)
        set_directory_security(target, UNSAFE_DIRECTORY_SDDL)
        authority = RetainedDirectoryAuthority(target)
        evidence = self.report["retainedDirectoryAuthority"] = {}
        try:
            before = registration()
            hashes = {name: digest(target / name) for name in EXECUTABLES}
            self.stage = "retained_directory_authority_rejected"
            result = self.command(self.stage, self.candidate, ["/S"], 90)
            require(result["exitCode"] != 0 and registration() == before,
                    "unsafe_legacy_directory_repair_accepted")
            require(all(digest(target / name) == value for name, value in hashes.items())
                    and directory_identity(target) == authority.identity,
                    "unsafe_legacy_preflight_modified_installation")
            require(authority.reset_old_acl() == 0, "retained_authority_fixture_lost_before_cleanup")
            self.mark(self.stage)
            self.stage = "unsafe_legacy_remove_with_retained_handle"
            result = self.command(self.stage, self.candidate, ["/S", "/REMOVE"], 180)
            require(result["exitCode"] == 0, "unsafe_legacy_maintenance_remove_failed")
            self.assert_no_service()
            require(registration() is None and registration(32) is None
                    and not self.owner_file.exists(), "unsafe_legacy_maintenance_identity_remains")
            self.assert_marker()
            self.recreate_with_retained_handle(target, authority, "unsafe_directory_removed_and_recreated", evidence)
        finally:
            authority.close()

    def empty_directory_authority(self, target: Path) -> None:
        require(not target.exists(), "fixture_empty_directory_preexists")
        target.mkdir()
        self.installed_paths.append(target)
        set_directory_security(target, UNSAFE_DIRECTORY_SDDL)
        authority = RetainedDirectoryAuthority(target)
        evidence = self.report["emptyDirectoryAuthority"] = {}
        try:
            self.recreate_with_retained_handle(target, authority, "empty_unsafe_directory_recreated", evidence)
        finally:
            authority.close()

    def denied_targets(self) -> None:
        unsafe = self.fixture_root / "unsafe parent"
        unsafe.mkdir()
        set_directory_security(unsafe, UNSAFE_DIRECTORY_SDDL)
        destination = self.fixture_root / "redirect target"
        destination.mkdir()
        self.redirect = self.fixture_root / "redirect"
        self.redirect.symlink_to(destination, target_is_directory=True)
        try:
            for parent, label in ((unsafe, "unsafe_parent"), (self.redirect, "reparse_parent")):
                self.stage = label + "_rejected_before_copy"
                target = parent / PRODUCT
                command = f'"{self.candidate}" /S /D={target}'
                result = self.command(self.stage, self.candidate, command, 90)
                require(result["exitCode"] != 0 and not target.exists(), self.stage + "_failed")
                require(registration() is None and service_snapshot().get("winError") == 1060,
                        "denied_install_created_identity")
                self.mark(self.stage)
        finally:
            self.redirect.unlink()
            self.redirect = None
            set_directory_security(unsafe)

    def unsafe_existing_install(self, target: Path) -> None:
        self.install_at(target, "existing_safe_custom_install")
        before = self.install_service("existing_safe_service")
        registered = registration()
        hashes = {name: digest(target / name) for name in EXECUTABLES}
        try:
            set_directory_security(target.parent, UNSAFE_DIRECTORY_SDDL)
            self.stage = "failed_upgrade_preserves_running_install"
            result = self.command(self.stage, self.candidate, ["/S"], 90)
            require(result["exitCode"] != 0, "unsafe_existing_upgrade_accepted")
            require(self.running_service() == before and registration() == registered,
                    "failed_upgrade_changed_running_install")
            require(all(digest(target / name) == value for name, value in hashes.items()),
                    "failed_upgrade_replaced_existing_files")
            self.mark(self.stage)
            self.uninstall_current("unsafe_existing_uninstall")
            self.mark("unsafe_install_can_uninstall")
        finally:
            set_directory_security(target.parent)

    def foreign_service(self, target: Path) -> None:
        import win32service

        self.install_at(target, "foreign_service_install")
        foreign = self.fixture_root / "other service"
        foreign.mkdir()
        set_directory_security(foreign)
        self.foreign_helper = foreign / self.helper.name
        shutil.copyfile(self.helper, self.foreign_helper)
        manager = win32service.OpenSCManager(None, None, win32service.SC_MANAGER_CREATE_SERVICE)
        try:
            service = win32service.CreateService(manager, SERVICE_NAME, "Mihomo Optimized TUN Service",
                win32service.SERVICE_QUERY_CONFIG, win32service.SERVICE_WIN32_OWN_PROCESS,
                win32service.SERVICE_DEMAND_START, win32service.SERVICE_ERROR_NORMAL,
                f'"{self.foreign_helper}"', None, 0, None, None, None)
            win32service.CloseServiceHandle(service)
        finally:
            win32service.CloseServiceHandle(manager)
        try:
            before = service_snapshot()
            uninstaller = target / "uninstall.exe"
            result = self.command("foreign_service_uninstall_rejected", uninstaller, uninstall_command(uninstaller), 90)
            require(result["exitCode"] != 0 and service_snapshot() == before,
                    "uninstall_removed_foreign_service")
            self.assert_candidate_files()
            self.mark("foreign_service_preserved")
        finally:
            self.remove_foreign_service()
        self.uninstall_current("foreign_service_safe_retry")

    def remove_foreign_service(self) -> None:
        if self.foreign_helper is None:
            return
        state = service_snapshot()
        if state.get("winError") == 1060:
            self.foreign_helper = None
            return
        expected = executable_path(state.get("binary", ""))
        require(expected == os.path.normcase(str(self.foreign_helper.resolve())),
                "foreign_fixture_service_identity_changed")
        original = self.helper
        try:
            self.helper = self.foreign_helper
            self.remove_owned_service()
        finally:
            self.helper = original
        self.foreign_helper = None

    def run(self) -> None:
        import win32api
        import win32security

        require(registration() is None and registration(32) is None
                and not self.default_directory.exists(), "preexisting_application_installation")
        require(service_snapshot().get("winError") == 1060, "preexisting_mihomo_service")
        self.owned_data = [self.config.parent, self.owner_file.parent.parent]
        require(all(not path.exists() for path in self.owned_data), "preexisting_mihomo_data")
        require(not self.fixture_root.exists(), "preexisting_path_fixture")
        token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32security.TOKEN_QUERY)
        try:
            self.sid = win32security.ConvertSidToStringSid(win32security.GetTokenInformation(token, win32security.TokenUser)[0])
        finally:
            token.Close()
        self.load_candidate()
        self.mutations_started = True
        self.fixture_root.mkdir()
        set_directory_security(self.fixture_root)
        self.config.parent.mkdir()
        self.config.write_text("# custom-install-" + uuid.uuid4().hex + "\nmode: rule\nrules:\n  - MATCH,DIRECT\n",
                               encoding="utf-8")
        self.marker_hash = digest(self.config)
        self.install_at(self.default_directory, "default_fresh_install")
        self.uninstall_current("default_never_service_uninstall")
        self.mark("default_never_service_uninstall")
        self.denied_targets()
        self.install_at(self.fixture_root / PRODUCT, "same_volume_install")
        self.tun_lifecycle("same_volume_tun")
        self.volume = DisposableVolume(self)
        root = self.volume.create()
        parent = root / "应用 程序"
        parent.mkdir()
        set_directory_security(parent)
        self.install_at(parent / PRODUCT, "separate_volume_unicode_install")
        self.tun_lifecycle("separate_volume_unicode_tun", check_desktop_update=True)
        self.custom_core_update(parent / "内核 更新")
        self.reproduce_and_repair(parent / "旧版 修复")
        self.missing_runtime(parent / "残留 清理")
        self.remove_broken_install(parent / "旧版 卸载")
        self.retained_directory_authority(parent / "旧目录 权限")
        self.empty_directory_authority(parent / "空目录 权限")
        self.unsafe_existing_install(self.fixture_root / "invalidated install")
        self.foreign_service(self.fixture_root / "foreign conflict")

    def cleanup(self) -> None:
        if not self.mutations_started:
            return
        failures = []
        def attempt(label, operation):
            try:
                operation()
            except Exception as error:
                failures.append({"stage": "cleanup", "code": label, "detail": failure_code(error)})
        if self.tun is not None:
            attempt("tun_fixture_cleanup", self.tun.cleanup)
            self.tun = None
        if self.redirect is not None:
            attempt("owned_redirect_remove", self.redirect.unlink)
            self.redirect = None
        for child in list(self.active_children):
            attempt("exact_child_stop", lambda child=child: self.stop_child(child))
        if self.foreign_helper is not None:
            attempt("foreign_fixture_service_remove", self.remove_foreign_service)
        if self.service_owned:
            attempt("owned_service_remove", self.remove_owned_service)
            attempt("owned_firewall_remove", self.remove_owned_firewall_rule)
        attempt("final_service_absence", lambda: require(service_snapshot().get("winError") == 1060,
                                                         "fixture_service_remains"))
        # Normal execution has already verified each real uninstaller. Only
        # failed runs use the native bounded cleanup inherited from the fixture.
        for directory in self.installed_paths:
            attempt("owned_process_absence", lambda directory=directory: require(
                not self.processes.at_paths([directory / name for name in EXECUTABLES]), "fixture_process_remains"))
        if not failures:
            for directory in self.installed_paths:
                attempt("owned_install_tree_remove", lambda directory=directory: remove_owned_tree(directory))
            for directory in self.owned_data:
                attempt("owned_data_remove", lambda directory=directory: remove_owned_tree(directory))
            attempt("owned_parent_remove", lambda: remove_owned_tree(self.fixture_root))
        if self.volume is not None:
            attempt("owned_volume_detach", self.volume.close)
        if self.diagnostic_directory is not None:
            attempt("owned_diagnostic_directory_remove", lambda: remove_owned_tree(self.diagnostic_directory))
        attempt("final_registration_absence", lambda: require(registration() is None and registration(32) is None,
                                                               "fixture_registration_remains"))
        self.report["diagnostics"].extend(failures)
        self.report["tests"]["final_cleanup"] = not failures
        self.write_report()


def main() -> int:
    acceptance = None
    artifacts = Path("artifacts/verification")
    try:
        require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true",
                "disposable_github_windows_runner_required")
        require(ctypes.windll.shell32.IsUserAnAdmin(), "elevated_runner_required_for_custom_install")
        artifacts.mkdir(parents=True, exist_ok=True)
        acceptance = CustomInstallAcceptance(artifacts)
        acceptance.write_report()
        acceptance.run()
    except Exception as error:
        print("FAIL: custom_install_" + failure_code(error), file=sys.stderr, flush=True)
        if acceptance:
            acceptance.report["diagnostics"].append({"stage": acceptance.stage, "code": failure_code(error)})
    finally:
        if acceptance:
            try:
                acceptance.cleanup()
            except Exception as error:
                acceptance.report["diagnostics"].append({"stage": "cleanup", "code": failure_code(error)})
            acceptance.report["passed"] = (all(acceptance.report["tests"].get(name) is True for name in REQUIRED_CASES)
                                           and not acceptance.report["diagnostics"])
            acceptance.write_report()
    return 0 if acceptance and acceptance.report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
