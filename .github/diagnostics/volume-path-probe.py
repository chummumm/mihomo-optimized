#!/usr/bin/env python3
"""Native volume-path experiment in the real RestrictedDesktop medium child."""

import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import traceback


# RestrictedDesktop deliberately strips GitHub/environment credentials. The
# diagnostic script has a fixed checkout-relative location in both processes.
ROOT = Path(os.environ.get("GITHUB_WORKSPACE") or Path(__file__).resolve().parents[3])
sys.path.insert(0, str(ROOT / "desktop" / "tests"))
from windows_smoke import RestrictedDesktop, require


def token_evidence(expected_sid: str | None = None, *, medium: bool = False) -> dict:
    import win32api
    import win32security

    token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32security.TOKEN_QUERY)
    try:
        sid = win32security.ConvertSidToStringSid(
            win32security.GetTokenInformation(token, win32security.TokenUser)[0])
        groups = win32security.GetTokenInformation(token, win32security.TokenGroups)
        admin = [flags for value, flags in groups
                 if win32security.ConvertSidToStringSid(value) == "S-1-5-32-544"]
        integrity = win32security.ConvertSidToStringSid(
            win32security.GetTokenInformation(token, win32security.TokenIntegrityLevel)[0])
        statistics = win32security.GetTokenInformation(token, win32security.TokenStatistics)
        debug = win32security.LookupPrivilegeValue(None, "SeDebugPrivilege")
        privileges = win32security.GetTokenInformation(token, win32security.TokenPrivileges)
        result = {
            "pid": os.getpid(), "userSid": sid,
            "tokenType": win32security.GetTokenInformation(token, win32security.TokenType),
            "administratorGroupCount": len(admin),
            "administratorEnabled": any(flags & win32security.SE_GROUP_ENABLED for flags in admin),
            "administratorDenyOnly": any(flags & win32security.SE_GROUP_USE_FOR_DENY_ONLY for flags in admin),
            "integritySid": integrity,
            "integrity": "medium" if integrity == "S-1-16-8192" else integrity,
            "seDebugPrivilegeEnabled": any(luid == debug and flags & win32security.SE_PRIVILEGE_ENABLED
                                           for luid, flags in privileges),
            "authenticationId": str(statistics["AuthenticationId"]),
            "sessionId": win32security.GetTokenInformation(token, win32security.TokenSessionId),
        }
        # Encode bit tests as booleans rather than the raw nonzero masks.
        for name in ("administratorEnabled", "administratorDenyOnly", "seDebugPrivilegeEnabled"):
            result[name] = bool(result[name])
        if expected_sid is not None:
            require(sid == expected_sid, "probe_child_user_sid_changed")
        if medium:
            require(result["tokenType"] == 1 and len(admin) == 1
                    and not result["administratorEnabled"] and result["administratorDenyOnly"]
                    and result["integrity"] == "medium" and not result["seDebugPrivilegeEnabled"],
                    "probe_child_not_actual_medium_filtered_primary")
        return result
    finally:
        token.Close()


class NativePaths:
    def __init__(self) -> None:
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        signatures = {
            "QueryDosDeviceW": ([wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD], wintypes.DWORD),
            "GetLogicalDriveStringsW": ([wintypes.DWORD, wintypes.LPWSTR], wintypes.DWORD),
            "GetLogicalDrives": ([], wintypes.DWORD),
            "GetDriveTypeW": ([wintypes.LPCWSTR], wintypes.UINT),
            "CreateFileW": ([wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                             wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE], wintypes.HANDLE),
            "GetFinalPathNameByHandleW": ([wintypes.HANDLE, wintypes.LPWSTR, wintypes.DWORD,
                                           wintypes.DWORD], wintypes.DWORD),
            "CloseHandle": ([wintypes.HANDLE], wintypes.BOOL),
            "DefineDosDeviceW": ([wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR], wintypes.BOOL),
        }
        for name, (arguments, result) in signatures.items():
            function = getattr(self.api, name)
            function.argtypes, function.restype = arguments, result

    @staticmethod
    def error(code: int) -> dict:
        return {"winError": code, "message": ctypes.FormatError(code)}

    def query(self, name: str) -> dict:
        buffer = ctypes.create_unicode_buffer(32768)
        ctypes.set_last_error(0)
        count = self.api.QueryDosDeviceW(name, buffer, len(buffer))
        if not count:
            return {"device": name, "success": False, **self.error(ctypes.get_last_error())}
        return {"device": name, "success": True, "targets": [value for value in buffer[:count].split("\0") if value]}

    def roots(self) -> list[str]:
        buffer = ctypes.create_unicode_buffer(256)
        count = self.api.GetLogicalDriveStringsW(len(buffer), buffer)
        require(0 < count < len(buffer), "probe_logical_drives_failed")
        return [value for value in buffer[:count].split("\0") if value]

    def probe(self, requested: str, kind: str) -> dict:
        row = {"requested": requested, "kind": kind,
               "driveType": self.api.GetDriveTypeW(requested),
               "queryDosDevice": self.query(requested[:2]),
               "createFile": {"desiredAccess": 0x80, "shareMode": 7, "creationDisposition": 3,
                              "flagsAndAttributes": 0x02000000 | 0x00200000},
               "getFinalPathFlags": 0}
        ctypes.set_last_error(0)
        handle = self.api.CreateFileW(requested, 0x80, 7, None, 3, 0x02000000 | 0x00200000, None)
        if handle == ctypes.c_void_p(-1).value:
            row["createFile"].update({"success": False, **self.error(ctypes.get_last_error())})
            return row
        row["createFile"]["success"] = True
        try:
            buffer = ctypes.create_unicode_buffer(32768)
            ctypes.set_last_error(0)
            count = self.api.GetFinalPathNameByHandleW(handle, buffer, len(buffer), 0)
            if not count:
                row["getFinalPath"] = {"success": False, **self.error(ctypes.get_last_error())}
            elif count >= len(buffer):
                row["getFinalPath"] = {"success": False, "requiredCharacters": count, "error": "path_too_long"}
            else:
                final = buffer.value
                dos = final.removeprefix("\\\\?\\")
                has_prefix = final.startswith("\\\\?\\")
                row["getFinalPath"] = {"success": True, "finalPath": final, "dosPath": dos,
                                       "hasExtendedDosPrefix": has_prefix,
                                       "equalsRequestedRoot": has_prefix and dos.lower() == requested.lower()}
        finally:
            require(self.api.CloseHandle(handle), "probe_close_file_handle_failed")
        return row

    def unused_letter(self) -> str:
        for letter in "XVWYZUTSRQPONMLKJIHGFE":
            bit = 1 << (ord(letter) - ord("A"))
            root = letter + ":\\"
            if self.api.GetLogicalDrives() & bit or self.api.GetDriveTypeW(root) != 1:
                continue
            query = self.query(letter + ":")
            # Only the elevated supervisor creates aliases, and its successful
            # device-map access lets it require a proven absent definition.
            if not query["success"] and query.get("winError") in (2, 3):
                return letter + ":"
        raise RuntimeError("No unused drive letter could be proven absent in the supervisor device map")


def alias_rejected(row: dict) -> bool:
    final = row.get("getFinalPath", {})
    return (row.get("driveType") == 3 and row["createFile"].get("success") is True
            and final.get("success") is True and final.get("hasExtendedDosPrefix") is True
            and final.get("equalsRequestedRoot") is False)


def child(report: Path, expected_sid: str, inputs: Path) -> int:
    api = NativePaths()
    result = {"passed": False, "token": {}, "fixedVolumes": [], "subst": [], "errors": []}

    def checkpoint() -> None:
        report.write_text(json.dumps(result, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")

    try:
        result["token"] = token_evidence(expected_sid, medium=True)
        checkpoint()
        supplied = json.loads(inputs.read_text(encoding="utf-8"))
        roots = supplied["realRoots"]
        aliases = supplied["aliases"]
        require(len(aliases) == 2 and len({row["alias"] for row in aliases}) == 2,
                "probe_owned_alias_inputs_invalid")
        result["visibleDriveRoots"] = api.roots()
        require("c:\\" in [root.lower() for root in roots], "probe_real_c_drive_missing")
        for root in roots:
            result["fixedVolumes"].append(api.probe(root, "existing_fixed_volume"))
            checkpoint()
        for supplied_alias in aliases:
            alias = supplied_alias["alias"]
            bit = 1 << (ord(alias[0]) - ord("A"))
            mask = api.api.GetLogicalDrives()
            row = {"alias": alias, "target": supplied_alias["target"],
                   "visibleInLogicalDrives": bool(mask & bit),
                   "probe": api.probe(alias + "\\", "supervisor_owned_subst")}
            result["subst"].append(row)
            checkpoint()
        require(all(row["driveType"] == 3 and row["createFile"].get("success")
                    and row.get("getFinalPath", {}).get("success")
                    and row["getFinalPath"]["equalsRequestedRoot"] for row in result["fixedVolumes"]),
                "probe_real_fixed_volume_not_accepted")
        require(all(row["visibleInLogicalDrives"] and alias_rejected(row["probe"])
                    for row in result["subst"]),
            "probe_subst_alias_not_distinguished")
        result["passed"] = True
    except Exception:
        result["errors"].append(traceback.format_exc())
    finally:
        checkpoint()
    return 0 if result["passed"] else 1


def parent() -> int:
    import win32security

    require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
            and ctypes.windll.shell32.IsUserAnAdmin(), "probe_requires_disposable_elevated_windows_runner")
    artifacts = ROOT / "artifacts" / "verification"
    artifacts.mkdir(parents=True, exist_ok=True)
    result = {"passed": False, "candidateSource": "31b974cd2cb7edc1f12d5b6d7cd0963ad121288e",
              "diagnosticCommit": os.environ["GITHUB_SHA"], "parentToken": token_evidence(),
              "ownedSubst": [], "cleanup": []}
    desktop = None
    process = None
    temporary = None
    owned = []
    api = NativePaths()
    try:
        roots = [root for root in api.roots() if api.api.GetDriveTypeW(root) == 3]
        result["parentQueryDosDevice"] = [api.query(root[:2]) for root in roots]
        desktop = RestrictedDesktop()
        result["requestedChildToken"] = desktop.check_token(desktop.token)
        temporary = tempfile.TemporaryDirectory(prefix="mihomo-volume-native-probe-")
        directory = Path(temporary.name).resolve()
        # Only this newly created scratch directory receives a user-write
        # grant so the same-SID medium child can publish its own evidence.
        descriptor = win32security.ConvertStringSecurityDescriptorToSecurityDescriptor(
            "O:" + desktop.sid + "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;" + desktop.sid + ")", 1)
        win32security.SetFileSecurity(str(directory), win32security.OWNER_SECURITY_INFORMATION
            | win32security.DACL_SECURITY_INFORMATION | win32security.PROTECTED_DACL_SECURITY_INFORMATION,
            descriptor)
        for target in ("C:\\Windows", "C:\\"):
            require(Path(target).is_dir(), "probe_protected_target_missing")
            alias = api.unused_letter()
            before = api.query(alias)
            bit = 1 << (ord(alias[0]) - ord("A"))
            mask = api.api.GetLogicalDrives()
            drive_type = api.api.GetDriveTypeW(alias + "\\")
            require(mask and not mask & bit and drive_type == 1
                    and not before["success"] and before.get("winError") in (2, 3),
                    "probe_alias_became_occupied")
            row = {"alias": alias, "target": target, "before": before,
                   "absenceEvidence": {"logicalDrivesMask": mask, "aliasBitPresent": False,
                                       "driveType": drive_type}}
            result["ownedSubst"].append(row)
            command = [str(Path(os.environ["SystemRoot"]) / "System32" / "subst.exe"), alias, target]
            completed = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True,
                                       timeout=15, check=False, creationflags=subprocess.CREATE_NO_WINDOW)
            row["create"] = {"exitCode": completed.returncode,
                             "stdout": completed.stdout.decode("utf-8", errors="replace"),
                             "stderr": completed.stderr.decode("utf-8", errors="replace")}
            require(completed.returncode == 0, "probe_supervisor_subst_creation_failed")
            after = api.query(alias)
            raw_target = (after.get("targets") or ["\\??\\" + target])[0]
            owned.append((alias, raw_target))
            row["createdDefinition"] = after
            require(after["success"] and len(after["targets"]) == 1,
                    "probe_supervisor_subst_definition_unavailable")
            row["parentProbe"] = api.probe(alias + "\\", "supervisor_owned_subst")
        inputs = directory / "inputs.json"
        inputs.write_text(json.dumps({"realRoots": roots, "aliases": result["ownedSubst"]}), encoding="utf-8")
        report = directory / "child.json"
        process = desktop.spawn([sys.executable, str(Path(__file__).resolve()), "--child", str(report),
                                 "--expected-sid", desktop.sid, "--inputs", str(inputs)], directory)
        result["childExitCode"] = process.wait(120)
        process.close()
        process = None
        if report.is_file():
            result["child"] = json.loads(report.read_text(encoding="utf-8"))
            result["sameAuthenticationId"] = (result["parentToken"]["authenticationId"]
                == result["child"].get("token", {}).get("authenticationId"))
            result["passed"] = (result["childExitCode"] == 0 and result["child"].get("passed") is True
                                and result["sameAuthenticationId"] and len(result["ownedSubst"]) == 2
                                and all(alias_rejected(row["parentProbe"]) for row in result["ownedSubst"]))
        else:
            result["error"] = "medium_child_did_not_write_evidence"
    except Exception:
        result["traceback"] = traceback.format_exc()
    finally:
        if process is not None:
            try:
                process.close()
            except Exception:
                result["processCleanupError"] = traceback.format_exc()
                result["passed"] = False
        for alias, target in reversed(owned):
            # Remove only the exact definition this parent created, after the
            # reading child exits; never alter a replacement mapping or its ACL.
            ctypes.set_last_error(0)
            removed = bool(api.api.DefineDosDeviceW(0xF, alias, target))
            row = {"alias": alias, "rawTarget": target, "removedExactDefinition": removed}
            if not removed:
                row.update(api.error(ctypes.get_last_error()))
            row["after"] = api.query(alias)
            mask = api.api.GetLogicalDrives()
            row["aliasBitPresentAfter"] = bool(mask & (1 << (ord(alias[0]) - ord("A"))))
            row["driveTypeAfter"] = api.api.GetDriveTypeW(alias + "\\")
            result["cleanup"].append(row)
            if (not removed or not mask or row["aliasBitPresentAfter"] or row["driveTypeAfter"] != 1
                    or row["after"].get("success") or row["after"].get("winError") not in (2, 3)):
                result["passed"] = False
        if temporary is not None:
            try:
                temporary.cleanup()
            except Exception:
                result["scratchCleanupError"] = traceback.format_exc()
                result["passed"] = False
        if desktop is not None:
            desktop.close()
        serialized = json.dumps(result, ensure_ascii=True, indent=2) + "\n"
        (artifacts / "volume-native-probe.json").write_text(serialized, encoding="utf-8")
        print(serialized, flush=True)
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--child", type=Path)
    parser.add_argument("--expected-sid")
    parser.add_argument("--inputs", type=Path)
    args = parser.parse_args()
    raise SystemExit(child(args.child, args.expected_sid, args.inputs) if args.child else parent())
