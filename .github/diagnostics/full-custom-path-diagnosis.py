#!/usr/bin/env python3
"""Original custom-path suite, with one explicitly unavailable baseline skipped."""

import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import sys
import traceback
import uuid

ROOT = Path(os.environ["GITHUB_WORKSPACE"])
sys.path.insert(0, str(ROOT / "desktop" / "tests"))

from custom_install_smoke import CustomInstallAcceptance, REQUIRED_CASES
from upgrade_smoke import digest, failure_code
from upgrade_tun_smoke import TunUpgradeAcceptance
from windows_smoke import require


SOURCE = "882d416355513b55409a879547d33ed825441418"
INSTALLER_SHA256 = "445cffd0ed5534a05c73008decb2e8f4e21b56d3c19669281312b277f4f8c14e"
SKIPPED_CASE = "custom_core_update"
SKIP_REASON = "The source-built core-update baseline was not uploaded with this candidate artifact."
ANCESTOR_MASK = 0x500D0150  # windows.rs SecurityBoundary::Ancestor, evidence only.


def common_programs_locations() -> dict:
    from win32com.shell import shell, shellcon

    locations = {}
    try:
        locations["CSIDL_COMMON_PROGRAMS"] = shell.SHGetFolderPath(
            0, shellcon.CSIDL_COMMON_PROGRAMS, None, 0)
    except Exception as error:
        locations["csidlError"] = str(error)

    class Guid(ctypes.Structure):
        _fields_ = [("data1", wintypes.DWORD), ("data2", wintypes.WORD),
                    ("data3", wintypes.WORD), ("data4", ctypes.c_ubyte * 8)]

    known = Guid.from_buffer_copy(uuid.UUID("0139d44e-6afe-49f2-8690-3dafcae6ffb8").bytes_le)
    api = ctypes.WinDLL("shell32")
    api.SHGetKnownFolderPath.argtypes = [ctypes.POINTER(Guid), wintypes.DWORD,
                                        wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
    api.SHGetKnownFolderPath.restype = ctypes.c_long
    memory = ctypes.WinDLL("ole32")
    memory.CoTaskMemFree.argtypes = [ctypes.c_void_p]
    memory.CoTaskMemFree.restype = None
    value = ctypes.c_void_p()
    result = api.SHGetKnownFolderPath(ctypes.byref(known), 0, None, ctypes.byref(value))
    try:
        if result < 0 or not value.value:
            locations["knownFolderError"] = "HRESULT 0x%08X" % (result & 0xFFFFFFFF)
        else:
            locations["FOLDERID_CommonPrograms"] = ctypes.wstring_at(value)
    finally:
        if value.value:
            memory.CoTaskMemFree(value)
    return locations


def acl_row(path: Path) -> dict:
    import stat
    import win32security

    row = {"path": str(path), "aces": []}
    try:
        metadata = path.lstat()
        row.update({"attributes": metadata.st_file_attributes,
                    "isDirectory": stat.S_ISDIR(metadata.st_mode),
                    "isFile": stat.S_ISREG(metadata.st_mode),
                    "isReparsePoint": bool(metadata.st_file_attributes & 0x400),
                    "links": metadata.st_nlink})
        information = win32security.OWNER_SECURITY_INFORMATION | win32security.DACL_SECURITY_INFORMATION
        descriptor = win32security.GetNamedSecurityInfo(str(path), win32security.SE_FILE_OBJECT, information)
        row["ownerSid"] = win32security.ConvertSidToStringSid(descriptor.GetSecurityDescriptorOwner())
        row["sddl"] = win32security.ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, information)
        dacl = descriptor.GetSecurityDescriptorDacl()
        row["nullDacl"] = dacl is None
        if dacl is not None:
            for index in range(dacl.GetAceCount()):
                value = dacl.GetAce(index)
                kind, flags = value[0]
                mask = value[1] if isinstance(value[1], int) else None
                ace = {"index": index, "type": kind, "flags": flags, "flagsHex": hex(flags),
                       "mask": mask, "maskHex": hex(mask) if mask is not None else None,
                       "inheritOnly": bool(flags & 0x8),
                       "ancestorForbiddenMaskOverlap": hex(mask & ANCESTOR_MASK) if mask is not None else None}
                try:
                    ace["sid"] = win32security.ConvertSidToStringSid(value[-1])
                except Exception as error:
                    ace["sidDecodeError"] = type(error).__name__
                row["aces"].append(ace)
    except Exception as error:
        row.update({"errorType": type(error).__name__, "error": str(error),
                    "winError": getattr(error, "winerror", None)})
    return row


class DiagnosticAcceptance(CustomInstallAcceptance):
    def capture_legacy_shortcut_stream(self) -> None:
        # Compatibility evidence only. This fixed path was just created by the
        # original broken_custom_install fixture; nothing is resolved or run.
        event = {"phase": "after_broken_custom_install", "readOnly": True,
                 "expectedTarget": str(self.install_dir / "mihomo-optimized.exe"),
                 "streamCompatible": False, "operation": "locate_fixture_shortcut",
                 "desiredAccess": "0x20081", "shareMode": "0x1",
                 "createFlags": "0x2200000", "pinnedPaths": []}
        initialized = False
        handles = []
        try:
            import pythoncom
            import win32file
            from win32com.shell import shell

            locations = common_programs_locations()
            shortcut = Path(locations["FOLDERID_CommonPrograms"]) / "Mihomo Optimized.lnk"
            event["shortcut"] = str(shortcut)
            for path in reversed([shortcut, *shortcut.parents]):
                event.update({"operation": "CreateFile_pin_path", "currentPath": str(path)})
                handle = win32file.CreateFile(str(path), 0x20081, 1, None, 3, 0x2200000, None)
                handles.append(handle)
                event["operation"] = "GetFileInformationByHandle"
                info = win32file.GetFileInformationByHandle(handle)
                attributes, links = info[0], info[7]
                length = (info[5] << 32) | info[6]
                event["pinnedPaths"].append({"path": str(path), "attributes": attributes,
                    "isDirectory": bool(attributes & 0x10), "isReparsePoint": bool(attributes & 0x400),
                    "links": links, "length": length,
                    "volumeSerial": info[4], "fileId": (info[8] << 32) | info[9]})
                leaf = path == shortcut
                if attributes & 0x400 or bool(attributes & 0x10) == leaf or (leaf and links != 1):
                    raise ValueError("Fixture shortcut chain has an unexpected file type or link")
            event["operation"] = "ReadFile_bounded_pinned_leaf"
            maximum = 1024 * 1024
            if not length or length > maximum:
                raise ValueError("Fixture shortcut must be nonempty and at most 1 MiB")
            status, contents = win32file.ReadFile(handles[-1], maximum + 1)
            if status != 0 or len(contents) != length:
                raise ValueError("Pinned fixture shortcut read must return its exact observed length")
            event.update({"bytes": len(contents), "sha256": hashlib.sha256(contents).hexdigest()})
            event["operation"] = "CoInitializeEx"
            pythoncom.CoInitializeEx(pythoncom.COINIT_APARTMENTTHREADED)
            initialized = True
            event["operation"] = "CreateStreamOnHGlobal_Write_Seek"
            stream = pythoncom.CreateStreamOnHGlobal(None, True)
            stream.Write(contents)
            stream.Seek(0, 0)
            event["operation"] = "CoCreateInstance_CLSID_ShellLink"
            link = pythoncom.CoCreateInstance(
                shell.CLSID_ShellLink, None, pythoncom.CLSCTX_INPROC_SERVER, shell.IID_IShellLink)
            event["operation"] = "IPersistStream_Load"
            persistent = link.QueryInterface(pythoncom.IID_IPersistStream)
            persistent.Load(stream)
            event["operation"] = "IShellLink_GetPath_SLGP_RAWPATH"
            raw_target, _ = link.GetPath(0x4)  # SLGP_RAWPATH; never Resolve or inspect an icon.
            event.update({"rawTarget": raw_target,
                          "exactTargetMatch": raw_target == event["expectedTarget"],
                          "streamCompatible": raw_target == event["expectedTarget"],
                          "operation": "complete"})
        except Exception as error:
            event.update({"errorType": type(error).__name__, "message": str(error),
                          "hresult": getattr(error, "hresult", None),
                          "winError": getattr(error, "winerror", None)})
        finally:
            if initialized:
                try:
                    persistent = link = stream = None
                    pythoncom.CoUninitialize()
                except Exception as error:
                    event["comCleanupError"] = str(error)
            event["closedHandleCount"] = 0
            for handle in reversed(handles):
                try:
                    handle.Close()
                    event["closedHandleCount"] += 1
                except Exception as error:
                    event.setdefault("handleCleanupErrors", []).append(str(error))
        # Do not let evidence persistence mask the original installer outcome.
        try:
            self.report.setdefault("legacyShortcutStreamProbes", []).append(event)
            self.write_report()
            (self.artifacts / "legacy-shortcut-stream.json").write_text(
                json.dumps(event, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
        except Exception as error:
            event["evidenceWriteError"] = str(error)
        print("LEGACY_SHORTCUT_STREAM " + json.dumps(event, ensure_ascii=True), flush=True)

    def capture_legacy_anchor_acl(self, phase: str) -> None:
        try:
            self._capture_legacy_anchor_acl(phase)
        except Exception as error:
            # Diagnostic collection must never replace an original assertion
            # or change the success/failure of the installer command.
            print("LEGACY_ANCHOR_ACL_CAPTURE_ERROR " + json.dumps(
                {"phase": phase, "type": type(error).__name__, "message": str(error)},
                ensure_ascii=True), flush=True)

    def _capture_legacy_anchor_acl(self, phase: str) -> None:
        event = {"phase": phase, "target": str(self.install_dir), "chains": [], "paths": [],
                 "productionAncestorMask": hex(ANCESTOR_MASK),
                 "productionTrustedOwnerSids": ["S-1-5-18", "S-1-5-32-544",
                    "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"]}
        try:
            locations = common_programs_locations()
            event["commonProgramsLocations"] = locations
            endpoints = [("installation_target", self.install_dir)]
            endpoints.extend((source, Path(locations[source]) / "Mihomo Optimized.lnk")
                             for source in ("CSIDL_COMMON_PROGRAMS", "FOLDERID_CommonPrograms")
                             if source in locations)
            paths = {}
            for source, endpoint in endpoints:
                chain = list(reversed([endpoint, *endpoint.parents]))
                event["chains"].append({"source": source, "paths": [str(path) for path in chain]})
                for path in chain:
                    paths.setdefault(str(path).casefold(), path)
            event["paths"] = [acl_row(path) for path in paths.values()]
        except Exception as error:
            event["snapshotError"] = str(error)
        self.report.setdefault("legacyAnchorAclSnapshots", []).append(event)
        self.write_report()
        (self.artifacts / "legacy-anchor-acl.json").write_text(
            json.dumps(self.report["legacyAnchorAclSnapshots"], ensure_ascii=True, indent=2) + "\n",
            encoding="utf-8")
        # This is observation, never a replacement gate or an ACL repair.
        summary = {**event, "paths": [{key: value for key, value in row.items() if key != "aces"}
                                      for row in event["paths"]]}
        print("LEGACY_ANCHOR_ACL " + json.dumps(summary, ensure_ascii=True), flush=True)

    def command(self, name: str, executable: Path, arguments, timeout: float = 90) -> dict:
        try:
            return super().command(name, executable, arguments, timeout)
        finally:
            if name in ("broken_custom_install", "broken_custom_uninstall", "repair_broken_custom_install"):
                self.capture_legacy_anchor_acl("after_" + name)
            if name == "broken_custom_install":
                self.capture_legacy_shortcut_stream()

    def load_candidate(self) -> None:
        super().load_candidate()
        require(self.info["uiCommit"] == SOURCE and self.info["desktopVersion"] == "0.2.14"
                and digest(self.candidate) == INSTALLER_SHA256,
                "diagnostic_candidate_not_exact_fourth_ci_artifact")

    def custom_core_update(self, target: Path) -> None:
        # This is the only scenario omitted. Do not mark it passed, construct
        # its missing baseline, or change any production fixture or gate.
        require(not self.report["skippedCases"], "diagnostic_skip_called_more_than_once")
        skipped = {"case": SKIPPED_CASE, "reason": SKIP_REASON, "plannedTarget": str(target)}
        self.report["skippedCases"].append(skipped)
        self.write_report()
        print("DIAGNOSTIC_ONLY_SKIP " + json.dumps(skipped, ensure_ascii=True), flush=True)


def failure_trace(error: Exception) -> str:
    # Use the existing TUN fixture's known-secret redaction policy without
    # retaining or displaying stack locals or unfiltered protocol responses.
    secrets = set()
    current = error.__traceback__
    while current is not None:
        for name in ("self", "tun"):
            value = current.tb_frame.f_locals.get(name)
            if isinstance(value, TunUpgradeAcceptance):
                secrets.update(getattr(value, "secrets", set()))
        current = current.tb_next
    text = "".join(traceback.format_exception(error))
    for secret in secrets:
        if secret:
            text = text.replace(secret, "[redacted]")
    return "".join(char for char in text if char in "\n\t" or ord(char) >= 32)


def main() -> int:
    os.chdir(ROOT)
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
    artifacts = ROOT / "artifacts" / "verification"
    artifacts.mkdir(parents=True, exist_ok=True)
    acceptance = None
    original_run_finished = False

    def record_failure(stage: str, error: Exception) -> None:
        text = failure_trace(error)
        with (artifacts / "full-custom-path-traceback.log").open("a", encoding="utf-8") as stream:
            stream.write(stage + "\n" + text + "\n")
        print(text, file=sys.stderr, flush=True)
        if acceptance is not None:
            acceptance.report["diagnostics"].append({
                "stage": stage, "code": failure_code(error), "traceback": text,
            })

    try:
        require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true",
                "disposable_github_windows_runner_required")
        require(ctypes.windll.shell32.IsUserAnAdmin(), "elevated_runner_required_for_custom_install")
        require(SKIPPED_CASE in REQUIRED_CASES, "diagnostic_skip_not_a_known_original_case")
        acceptance = DiagnosticAcceptance(artifacts)
        acceptance.report.update({
            "passed": False,
            "partialDiagnostic": True,
            "fullSuiteExecuted": False,
            "diagnosticScope": "original CustomInstallAcceptance.run except custom_core_update",
            "plannedSkippedCases": [{"case": SKIPPED_CASE, "reason": SKIP_REASON}],
            "skippedCases": [],
            "diagnosticRequiredCases": sorted(REQUIRED_CASES - {SKIPPED_CASE}),
            "candidateSource": SOURCE,
            "candidateRunId": 45658312465,
            "candidateArtifactId": 15046225380,
            "diagnosticCommit": os.environ["GITHUB_SHA"],
        })
        acceptance.write_report()
        acceptance.capture_legacy_anchor_acl("before_run")
        # The original run method owns every preflight, path case, real
        # installer/service/TUN operation, repair and removal assertion.
        acceptance.run()
        original_run_finished = True
    except Exception as error:
        record_failure(acceptance.stage if acceptance else "preflight", error)
    finally:
        if acceptance is not None:
            try:
                acceptance.cleanup()
            except Exception as error:
                record_failure("cleanup", error)
            acceptance.report["passed"] = False
            skipped = [row["case"] for row in acceptance.report["skippedCases"]]
            acceptance.report["diagnosticPassed"] = (
                original_run_finished and skipped == [SKIPPED_CASE]
                and SKIPPED_CASE not in acceptance.report["tests"]
                and all(acceptance.report["tests"].get(name) is True
                        for name in REQUIRED_CASES - {SKIPPED_CASE})
                and not acceptance.report["diagnostics"]
            )
            acceptance.write_report()
            print(json.dumps({key: acceptance.report[key] for key in
                  ("partialDiagnostic", "fullSuiteExecuted", "passed", "diagnosticPassed",
                   "plannedSkippedCases", "skippedCases", "tests")},
                  ensure_ascii=True, indent=2), flush=True)
    return 0 if acceptance is not None and acceptance.report.get("diagnosticPassed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
