#!/usr/bin/env python3
"""Acceptance test for the installed Windows application on a disposable runner.

Uses actual WebView2/EdgeDriver, WinInet, pywin32 firewall COM, and a local SOCKS
fixture. No Tauri bridge or operating-system behavior is mocked.
See https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/webdriver
and https://learn.microsoft.com/en-us/microsoft-edge/webdriver/capabilities-edge-options

setup-windows.ps1 supplies MIHOMO_TEST_APP and MIHOMO_TEST_DRIVER. This test changes
the runner's own proxy/autostart settings and installs the app's TUN service; it
refuses to run outside a disposable GitHub Actions Windows runner.
An elevated supervisor handles installation/cleanup. EdgeDriver and the actual
GUI use a same-SID filtered primary token at medium integrity; this is not an
interactive UAC-consent test or a separate standard-user-account test.
"""

from __future__ import annotations

import base64
import contextlib
import ctypes
from ctypes import wintypes
from dataclasses import dataclass
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import socket
import socketserver
import struct
import subprocess
import sys
import threading
import time
from urllib.parse import quote, urlsplit


SERVICE_NAME = "MihomoOptimizedService"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE = "MihomoOptimized"
TUN_TARGET = "198.18.77.77"
TUN_BODY = b"mihomo-windows-tun-socks-forwarded\n"
BAD_CONFIG = "mixed-port: [unterminated\n"
FIREWALL_RULE_PREFIX = "com.chummumm.mihomo-optimized.tun.tcp."
FIREWALL_GROUP = "Mihomo Optimized TUN"


class SmokeFailure(Exception):
    """An intentionally bounded diagnostic code, never a native error payload."""


def require(condition: object, code: str) -> None:
    if not condition:
        raise SmokeFailure(code)


def safe_code(value: object) -> str:
    return value if isinstance(value, str) and re.fullmatch(r"[a-zA-Z0-9_. -]{1,100}", value) else "unspecified_error"


def wait_until(predicate, code: str, timeout: float = 45.0):
    deadline = time.monotonic() + timeout
    while True:
        result = predicate()
        if result:
            return result
        if time.monotonic() >= deadline:
            raise SmokeFailure(code)
        time.sleep(0.1)


def free_port() -> int:
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def listening(port: int) -> bool:
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=0.25):
            return True
    except OSError:
        return False


def child_environment() -> dict[str, str]:
    # No GitHub credentials or unrelated proxy/core overrides reach the fixture
    # application. Windows/WebView2 still receive their standard runtime paths.
    allowed = {
        "SYSTEMROOT", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP", "SYSTEMDRIVE",
        "COMSPEC", "APPDATA", "LOCALAPPDATA", "USERPROFILE", "USERNAME",
        "USERDOMAIN", "SESSIONNAME", "PROGRAMFILES", "PROGRAMFILES(X86)",
        "PROGRAMW6432", "COMMONPROGRAMFILES", "COMMONPROGRAMFILES(X86)",
        "COMMONPROGRAMW6432", "ALLUSERSPROFILE", "PROGRAMDATA",
    }
    return {key: value for key, value in os.environ.items() if key.upper() in allowed}


class OptionValue(ctypes.Union):
    _fields_ = [("number", wintypes.DWORD), ("string", ctypes.c_void_p), ("time", wintypes.FILETIME)]


class ConnectionOption(ctypes.Structure):
    _fields_ = [("option", wintypes.DWORD), ("value", OptionValue)]


class ConnectionOptions(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("connection", wintypes.LPWSTR),
                ("count", wintypes.DWORD), ("error", wintypes.DWORD),
                ("options", ctypes.POINTER(ConnectionOption))]


class WinInet:
    """Read and restore the same per-connection logical fields as the native app."""

    def __init__(self) -> None:
        self.api = ctypes.WinDLL("wininet", use_last_error=True)
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.InternetQueryOptionW.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD)]
        self.api.InternetQueryOptionW.restype = wintypes.BOOL
        self.api.InternetSetOptionW.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
        self.api.InternetSetOptionW.restype = wintypes.BOOL
        self.kernel.GlobalFree.argtypes = [ctypes.c_void_p]
        self.kernel.GlobalFree.restype = ctypes.c_void_p

    @staticmethod
    def options():
        items = (ConnectionOption * 4)()
        for index, item in enumerate(items):
            item.option = index + 1  # FLAGS, PROXY_SERVER, PROXY_BYPASS, AUTOCONFIG_URL
        return items, ConnectionOptions(ctypes.sizeof(ConnectionOptions), None, 4, 0, items)

    def read(self) -> dict:
        items, request = self.options()
        length = wintypes.DWORD(ctypes.sizeof(request))
        require(self.api.InternetQueryOptionW(None, 75, ctypes.byref(request), ctypes.byref(length)), "wininet_query_failed")
        try:
            strings = [ctypes.wstring_at(item.value.string) if item.value.string else "" for item in items[1:]]
            return dict(zip(("flags", "server", "bypass", "pac"), [items[0].value.number, *strings]))
        finally:
            for item in items[1:]:
                if item.value.string:
                    self.kernel.GlobalFree(item.value.string)

    def restore(self, settings: dict) -> None:
        items, request = self.options()
        items[0].value.number = settings["flags"]
        buffers = [ctypes.create_unicode_buffer(settings[name]) for name in ("server", "bypass", "pac")]
        for item, buffer in zip(items[1:], buffers):
            item.value.string = ctypes.addressof(buffer)
        require(self.api.InternetSetOptionW(None, 75, ctypes.byref(request), ctypes.sizeof(request)), "wininet_restore_failed")
        require(self.api.InternetSetOptionW(None, 39, None, 0), "wininet_notify_failed")
        require(self.api.InternetSetOptionW(None, 37, None, 0), "wininet_refresh_failed")


class ProcessEntry(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD), ("pid", wintypes.DWORD),
                ("heap", ctypes.c_size_t), ("module", wintypes.DWORD), ("threads", wintypes.DWORD),
                ("parent", wintypes.DWORD), ("priority", wintypes.LONG), ("flags", wintypes.DWORD),
                ("name", wintypes.WCHAR * 260)]


@dataclass(frozen=True)
class ProcessIdentity:
    pid: int
    path: str
    created: int


class Processes:
    """Never terminate by executable name: verify the path and creation time."""

    def __init__(self) -> None:
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        signatures = {
            "CreateToolhelp32Snapshot": ([wintypes.DWORD, wintypes.DWORD], wintypes.HANDLE),
            "Process32FirstW": ([wintypes.HANDLE, ctypes.POINTER(ProcessEntry)], wintypes.BOOL),
            "Process32NextW": ([wintypes.HANDLE, ctypes.POINTER(ProcessEntry)], wintypes.BOOL),
            "OpenProcess": ([wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE),
            "CloseHandle": ([wintypes.HANDLE], wintypes.BOOL),
            "QueryFullProcessImageNameW": ([wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)], wintypes.BOOL),
            "GetProcessTimes": ([wintypes.HANDLE, *([ctypes.POINTER(wintypes.FILETIME)] * 4)], wintypes.BOOL),
            "TerminateProcess": ([wintypes.HANDLE, wintypes.UINT], wintypes.BOOL),
            "WaitForSingleObject": ([wintypes.HANDLE, wintypes.DWORD], wintypes.DWORD),
        }
        for name, (arguments, result) in signatures.items():
            function = getattr(self.api, name)
            function.argtypes, function.restype = arguments, result

    def entries(self) -> list[tuple[int, int, str]]:
        handle = self.api.CreateToolhelp32Snapshot(2, 0)
        require(handle and handle != ctypes.c_void_p(-1).value, "process_snapshot_failed")
        try:
            entry = ProcessEntry()
            entry.size = ctypes.sizeof(entry)
            result = []
            available = self.api.Process32FirstW(handle, ctypes.byref(entry))
            while available:
                result.append((entry.pid, entry.parent, entry.name))
                available = self.api.Process32NextW(handle, ctypes.byref(entry))
            return result
        finally:
            self.api.CloseHandle(handle)

    def from_handle(self, handle, pid: int) -> ProcessIdentity | None:
        path = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(path))
        times = [wintypes.FILETIME() for _ in range(4)]
        if not self.api.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(size)):
            return None
        if not self.api.GetProcessTimes(handle, *(ctypes.byref(value) for value in times)):
            return None
        return ProcessIdentity(pid, os.path.normcase(os.path.abspath(path.value)),
                               (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime)

    def identity(self, pid: int) -> ProcessIdentity | None:
        handle = self.api.OpenProcess(0x1000, False, pid)
        if not handle:
            return None
        try:
            return self.from_handle(handle, pid)
        finally:
            self.api.CloseHandle(handle)

    def at_paths(self, paths: list[Path]) -> list[ProcessIdentity]:
        expected = {os.path.normcase(str(path.resolve())) for path in paths}
        names = {path.name.lower() for path in paths}
        result = []
        for pid, _parent, name in self.entries():
            if name.lower() in names:
                identity = self.identity(pid)
                if identity and identity.path in expected:
                    result.append(identity)
        return result

    def descendants(self, roots: list[ProcessIdentity], allowed_paths: set[str]) -> list[ProcessIdentity]:
        # Toolhelp parent IDs outlive the parent. A later process may reuse that
        # ID, so every edge needs a verified parent identity and creation order.
        # Only known application/WebView2 binaries may enter the cleanup set.
        entries = self.entries()
        selected = {root.pid: root for root in roots if self.identity(root.pid) == root}
        root_ids = set(selected)
        pending = list(selected.values())
        while pending:
            parent = pending.pop()
            if self.identity(parent.pid) != parent:
                continue
            for pid, parent_pid, _name in entries:
                if parent_pid != parent.pid or pid in selected:
                    continue
                child = self.identity(pid)
                if (child and child.path in allowed_paths and child.created >= parent.created
                        and self.identity(parent.pid) == parent):
                    selected[pid] = child
                    pending.append(child)
        return [identity for pid, identity in selected.items() if pid not in root_ids]

    def kill(self, expected: ProcessIdentity) -> None:
        handle = self.api.OpenProcess(0x1000 | 0x100000 | 0x1, False, expected.pid)
        if not handle:
            require(self.identity(expected.pid) is None, "process_open_for_termination_failed")
            return
        try:
            require(self.from_handle(handle, expected.pid) == expected, "process_identity_changed_before_termination")
            require(self.api.TerminateProcess(handle, 87), "exact_process_termination_failed")
            require(self.api.WaitForSingleObject(handle, 10000) == 0, "exact_process_did_not_exit")
        finally:
            self.api.CloseHandle(handle)


class RestrictedProcess:
    """Own the exact process handle returned by CreateProcessAsUser."""

    def __init__(self, handle, pid: int, arguments: list[str]) -> None:
        self.handle, self.pid, self.arguments = handle, pid, arguments
        self.returncode = None

    def poll(self) -> int | None:
        import win32event
        import win32process

        if self.handle is not None:
            state = win32event.WaitForSingleObject(self.handle, 0)
            require(state in (win32event.WAIT_OBJECT_0, win32event.WAIT_TIMEOUT), "restricted_process_wait_failed")
            if state == win32event.WAIT_OBJECT_0:
                self.returncode = win32process.GetExitCodeProcess(self.handle)
        return self.returncode

    def wait(self, timeout: float) -> int:
        import win32event

        if self.poll() is None:
            state = win32event.WaitForSingleObject(self.handle, int(timeout * 1000))
            if state == win32event.WAIT_TIMEOUT:
                raise subprocess.TimeoutExpired(self.arguments, timeout)
            require(state == win32event.WAIT_OBJECT_0, "restricted_process_wait_failed")
        result = self.poll()
        require(result is not None, "restricted_process_exit_code_missing")
        return result

    def close(self) -> None:
        import pywintypes
        import win32process

        if self.handle is None:
            return
        try:
            if self.poll() is None:
                try:
                    win32process.TerminateProcess(self.handle, 87)
                except pywintypes.error:
                    # The owned process may have exited since poll().
                    if self.poll() is None:
                        raise
                self.wait(5)
        finally:
            self.handle.Close()
            self.handle = None


class RestrictedDesktop:
    """Simulate a filtered administrator of the same SID, not a second account.

    Hosted Windows runners disable UAC, so a linked token is not assumed. The
    administrator supervisor retains installation and cleanup responsibilities.
    """

    def __init__(self) -> None:
        import pywintypes
        import win32api
        import win32security

        self.token = None
        source = None
        stage = "open_supervisor_token"
        try:
            source = win32security.OpenProcessToken(win32api.GetCurrentProcess(),
                win32security.TOKEN_QUERY | win32security.TOKEN_DUPLICATE
                | win32security.TOKEN_ASSIGN_PRIMARY | win32security.TOKEN_ADJUST_DEFAULT)
            user = win32security.GetTokenInformation(source, win32security.TokenUser)[0]
            self.sid = win32security.ConvertSidToStringSid(user)
            self.source_context = self.token_context(source)
            administrator = win32security.CreateWellKnownSid(win32security.WinBuiltinAdministratorsSid, None)
            stage = "create_restricted_token"
            # LUA_TOKEN is 0x4 in winnt.h; pywin32 311 does not export that flag.
            self.token = win32security.CreateRestrictedToken(source,
                win32security.DISABLE_MAX_PRIVILEGE | 0x4, [(administrator, 0)], None, None)
            stage = "set_medium_integrity"
            medium = win32security.ConvertStringSidToSid("S-1-16-8192")
            win32security.SetTokenInformation(self.token, win32security.TokenIntegrityLevel,
                                             (medium, win32security.SE_GROUP_INTEGRITY))
            self.check_token(self.token)
        except BaseException as error:
            self.close()
            if isinstance(error, pywintypes.error):
                raise SmokeFailure(f"{stage}_winerror_{error.winerror}") from None
            raise
        finally:
            if source is not None:
                source.Close()

    @staticmethod
    def token_context(token) -> dict:
        import win32security

        logon_groups = []
        for sid, attributes in win32security.GetTokenInformation(token, win32security.TokenGroups):
            attributes = int(attributes) & 0xFFFFFFFF
            if attributes & 0xC0000000 == 0xC0000000:  # SE_GROUP_LOGON_ID
                logon_groups.append({"sid": win32security.ConvertSidToStringSid(sid),
                                     "attributesHex": f"0x{attributes:08X}",
                                     "enabled": bool(attributes & 0x4),
                                     "denyOnly": bool(attributes & 0x10)})
        return {"sessionId": win32security.GetTokenInformation(token, win32security.TokenSessionId),
                "logonGroups": logon_groups}

    def probe_access(self, output: Path) -> dict:
        """Observe desktop access without changing any ACL or launch setting."""
        import pywintypes
        import win32api
        import win32con
        import win32security
        import win32service

        evidence = {"sourceToken": self.source_context, "restrictedToken": self.token_context(self.token),
                    "cases": []}
        parent_station = parent_desktop = None
        stage = "parent_window_station"

        def close_owned(handle, method):
            getattr(handle, method)()
            # These pywin32 311 methods do not invalidate the wrapper themselves.
            handle.Detach()

        def probe(api, mask, open_handle, close_method):
            result = {"api": api, "mask": f"0x{mask:08X}", "opened": False}
            handle = None
            operation = "ImpersonateLoggedOnUser"
            try:
                try:
                    win32security.ImpersonateLoggedOnUser(self.token)
                    operation = api
                    handle = open_handle(mask)
                    result["opened"] = True
                    result["winError"] = 0
                except pywintypes.error as error:
                    result.update({"failedApi": operation, "winError": error.winerror})
                finally:
                    try:
                        win32security.RevertToSelf()
                    except pywintypes.error as error:
                        raise SmokeFailure(f"desktop_probe_revert_winerror_{error.winerror}") from None
            finally:
                if handle is not None:
                    close_owned(handle, close_method)
            return result

        try:
            parent_station = win32service.GetProcessWindowStation()
            station_name = win32service.GetUserObjectInformation(parent_station, win32con.UOI_NAME)
            stage = "parent_thread_desktop"
            parent_desktop = win32service.GetThreadDesktop(win32api.GetCurrentThreadId())
            desktop_name = win32service.GetUserObjectInformation(parent_desktop, win32con.UOI_NAME)
            evidence["parent"] = {"windowStation": station_name, "desktop": desktop_name}
            targets = [(station_name, desktop_name, "parent"), ("winsta0", "default", "requested")]
            for station, desktop, label in targets:
                case = {"context": label, "windowStation": station, "desktop": desktop,
                        "windowStationAccess": [], "desktopAccess": []}
                evidence["cases"].append(case)
                # Higher masks diagnose additional rights; denial is evidence,
                # not an assertion that GUI startup requires every listed right.
                for level, mask in (("basic", 0x23), ("runtime", 0x37F), ("full", 0xF037F)):
                    result = probe("OpenWindowStation", mask,
                                   lambda access: win32service.OpenWindowStation(station, False, access),
                                   "CloseWindowStation")
                    case["windowStationAccess"].append({"level": level, **result})
                target_station = None
                switched = False
                operation = "OpenWindowStation_supervisor"
                try:
                    if station.casefold() != station_name.casefold():
                        # OpenDesktop resolves against the PROCESS station.
                        # Bind briefly as supervisor; every access check below
                        # still impersonates the exact restricted primary token.
                        case["bindingMask"] = "0x00020003"  # READ_CONTROL, enum, read attributes
                        target_station = win32service.OpenWindowStation(station, False, 0x20003)
                        operation = "SetProcessWindowStation"
                        target_station.SetProcessWindowStation()
                        switched = True
                    for level, mask in (("basic", 0x83), ("runtime", 0x1FF), ("full", 0xF01FF)):
                        result = probe("OpenDesktop", mask,
                                       lambda access: win32service.OpenDesktop(desktop, 0, False, access),
                                       "CloseDesktop")
                        case["desktopAccess"].append({"level": level, **result})
                except pywintypes.error as error:
                    case["bindingError"] = {"api": operation, "winError": error.winerror}
                finally:
                    try:
                        if switched:
                            try:
                                parent_station.SetProcessWindowStation()
                                case["parentStationRestored"] = True
                            except pywintypes.error as error:
                                case["restoreError"] = error.winerror
                                # Never close a handle that is still current.
                                target_station.Detach()
                                target_station = None
                                raise SmokeFailure(f"desktop_station_restore_winerror_{error.winerror}") from None
                    finally:
                        if target_station is not None:
                            close_owned(target_station, "CloseWindowStation")
        except pywintypes.error as error:
            evidence["diagnosticError"] = {"stage": stage, "winError": error.winerror}
        finally:
            # Get* returns borrowed handles wrapped with owning destructors.
            if parent_desktop is not None:
                parent_desktop.Detach()
            if parent_station is not None:
                parent_station.Detach()
            output.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
        return evidence

    def check_token(self, token) -> dict:
        import win32security

        user = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
        require(win32security.ConvertSidToStringSid(user) == self.sid, "restricted_user_sid_changed")
        require(win32security.GetTokenInformation(token, win32security.TokenType) == 1,
                "restricted_token_not_primary")
        groups = win32security.GetTokenInformation(token, win32security.TokenGroups)
        administrators = [attributes for sid, attributes in groups
                          if win32security.ConvertSidToStringSid(sid) == "S-1-5-32-544"]
        require(len(administrators) == 1
                and not administrators[0] & win32security.SE_GROUP_ENABLED
                and administrators[0] & win32security.SE_GROUP_USE_FOR_DENY_ONLY,
                "gui_administrator_sid_not_deny_only")
        integrity = win32security.GetTokenInformation(token, win32security.TokenIntegrityLevel)[0]
        require(win32security.ConvertSidToStringSid(integrity) == "S-1-16-8192", "gui_not_medium_integrity")
        debug = win32security.LookupPrivilegeValue(None, "SeDebugPrivilege")
        privileges = win32security.GetTokenInformation(token, win32security.TokenPrivileges)
        debug_enabled = any(luid == debug and attributes & win32security.SE_PRIVILEGE_ENABLED
                            for luid, attributes in privileges)
        require(not debug_enabled, "gui_debug_privilege_enabled")
        return {"mode": "same_sid_filtered_primary", "sameUserSid": True,
                "administratorEnabled": False, "administratorDenyOnly": True,
                "integrity": "medium", "seDebugPrivilegeEnabled": False,
                "sessionId": win32security.GetTokenInformation(token, win32security.TokenSessionId)}

    def check_process(self, process) -> dict:
        import win32security

        token = win32security.OpenProcessToken(process, win32security.TOKEN_QUERY)
        try:
            return self.check_token(token)
        finally:
            token.Close()

    def check_gui(self, expected: ProcessIdentity, processes: Processes) -> dict:
        import win32api

        process = win32api.OpenProcess(0x1000, False, expected.pid)
        try:
            require(processes.from_handle(int(process), expected.pid) == expected, "gui_identity_changed_before_token_check")
            return self.check_process(process)
        finally:
            process.Close()

    def spawn(self, arguments: list[str], cwd: Path) -> RestrictedProcess:
        import pywintypes
        import win32con
        import win32process

        process = thread = None
        stage = "create_restricted_process"
        try:
            startup = win32process.STARTUPINFO()
            startup.lpDesktop = r"winsta0\default"
            # No supervisor handles or ambient credentials reach the child.
            # Start suspended to inspect even a short-lived second instance.
            process, thread, pid, _tid = win32process.CreateProcessAsUser(
                self.token, arguments[0], subprocess.list2cmdline(arguments), None, None, False,
                win32con.CREATE_SUSPENDED | win32con.CREATE_NO_WINDOW,
                child_environment(), str(cwd), startup)
            stage = "inspect_restricted_process"
            self.check_process(process)
            stage = "resume_restricted_process"
            require(win32process.ResumeThread(thread) == 1, "restricted_process_resume_failed")
            result = RestrictedProcess(process, pid, arguments)
            process = None  # Ownership moves to the wrapper.
            return result
        except pywintypes.error as error:
            raise SmokeFailure(f"{stage}_winerror_{error.winerror}") from None
        finally:
            if thread is not None:
                thread.Close()
            if process is not None:
                RestrictedProcess(process, pid, arguments).close()

    def close(self) -> None:
        if self.token is not None:
            self.token.Close()
            self.token = None


def read_autostart():
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            return winreg.QueryValueEx(key, RUN_VALUE)
    except FileNotFoundError:
        return None


def restore_autostart(value) -> None:
    import winreg
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
        if value is None:
            with contextlib.suppress(FileNotFoundError):
                winreg.DeleteValue(key, RUN_VALUE)
        else:
            winreg.SetValueEx(key, RUN_VALUE, 0, value[1], value[0])


def service_installed() -> bool:
    result = subprocess.run(["sc.exe", "query", SERVICE_NAME], stdin=subprocess.DEVNULL,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
    require(result.returncode in (0, 1060), "service_query_failed")
    return result.returncode == 0


def com_error_hresult(error) -> int:
    code = error.hresult & 0xFFFFFFFF
    # IDispatch can wrap the actual COM error in DISP_E_EXCEPTION/EXCEPINFO.
    info = getattr(error, "excepinfo", None)
    if code == 0x80020009 and isinstance(info, tuple) and len(info) > 5 and isinstance(info[5], int) and info[5]:
        return info[5] & 0xFFFFFFFF
    return code


def owned_firewall_rule(core: Path) -> dict | None:
    import pythoncom
    import pywintypes
    from win32com.client import dynamic

    path = str(core.resolve()).removeprefix("\\\\?\\")
    name = FIREWALL_RULE_PREFIX + hashlib.sha256(path.lower().encode("utf-8")).hexdigest()[:16]
    policy = rules = rule = None
    pythoncom.CoInitializeEx(pythoncom.COINIT_APARTMENTTHREADED)
    try:
        # Use the same exact Item lookup as the native host. Do not enumerate
        # Windows firewall rules or start PowerShell/CIM for this assertion.
        policy = dynamic.Dispatch("HNetCfg.FwPolicy2")
        rules = policy.Rules
        try:
            rule = rules.Item(name)
        except pywintypes.com_error as error:
            if com_error_hresult(error) == 0x80070002:
                return None
            raise
        require(rule.Name == name and rule.Grouping == FIREWALL_GROUP
                and str(rule.ApplicationName).lower() == path.lower(), "owned_firewall_rule_identity_mismatch")
        return {"name": name, "group": FIREWALL_GROUP, "program": path,
                "enabled": bool(rule.Enabled), "direction": int(rule.Direction),
                "action": int(rule.Action), "profiles": int(rule.Profiles),
                "protocol": int(rule.Protocol), "edgeTraversal": bool(rule.EdgeTraversal)}
    except pywintypes.com_error as error:
        raise SmokeFailure(f"owned_firewall_query_hresult_{com_error_hresult(error):08X}") from None
    finally:
        rule = rules = policy = None
        pythoncom.CoUninitialize()


def owned_firewall_rule_count(core: Path) -> int:
    rule = owned_firewall_rule(core)
    if rule is None:
        return 0
    require(rule["enabled"] and rule["direction"] == 1 and rule["action"] == 1
            and rule["profiles"] == 0x7FFFFFFF and rule["protocol"] == 6 and not rule["edgeTraversal"],
            "owned_firewall_rule_settings_mismatch")
    return 1


class WebViewDriver:
    def __init__(self, driver: Path, app: Path, processes: Processes, desktop: RestrictedDesktop,
                 artifacts: Path) -> None:
        self.port = free_port()
        self.session = None
        self.log_path = (artifacts / "edgedriver.log").resolve()
        self.startup_path = artifacts / "edgedriver-startup.json"
        self.startup = {"ready": False, "requestedDesktop": r"winsta0\default",
                        "requestedToken": desktop.check_token(desktop.token),
                        "desktopAccessFile": "desktop-access.json",
                        "logFile": self.log_path.name, "logLevel": "SEVERE"}
        self.write_startup_evidence()
        # EdgeDriver inherits Chromium's mutually exclusive log-level/silent flags.
        # Keep protocol command/response logging disabled at SEVERE level.
        self.process = desktop.spawn([str(driver), f"--port={self.port}", "--log-level=SEVERE",
                                      "--log-path=" + str(self.log_path)], driver.parent)
        try:
            self.identity = processes.identity(self.process.pid)
            require(self.identity and self.identity.path == os.path.normcase(str(driver.resolve())), "edgedriver_process_mismatch")
            wait_until(self.ready, "edgedriver_start_timeout", 20)
            self.startup["ready"] = True
            self.write_startup_evidence()
            result = self.request("POST", "/session", {"capabilities": {"alwaysMatch": {
                "browserName": "webview2", "ms:edgeOptions": {"binary": str(app)},
            }}}, timeout=90)
            require(isinstance(result, dict) and isinstance(result.get("sessionId"), str), "webview_session_missing")
            self.session = result["sessionId"]
            self.request("POST", self.path("timeouts"), {"script": 90000, "pageLoad": 30000, "implicit": 0})
        except BaseException as error:
            try:
                self.startup["failure"] = safe_code(str(error)) if isinstance(error, SmokeFailure) else "unexpected_" + type(error).__name__
                # Capture the real exit code before cleanup can terminate the driver.
                code = self.process.poll()
                if code is not None:
                    self.startup["exitCodeUnsigned"] = code & 0xFFFFFFFF
                    self.startup["exitCodeHex"] = f"0x{code & 0xFFFFFFFF:08X}"
                self.write_startup_evidence()
            finally:
                self.close()
            raise

    def write_startup_evidence(self) -> None:
        self.startup["logExists"] = self.log_path.is_file()
        self.startup_path.write_text(json.dumps(self.startup, indent=2) + "\n", encoding="utf-8")

    def ready(self) -> bool:
        code = self.process.poll()
        if code is not None:
            raise SmokeFailure(f"edgedriver_exited_0x{code & 0xFFFFFFFF:08X}")
        try:
            return self.request("GET", "/status", timeout=1).get("ready") is True
        except (OSError, http.client.HTTPException):
            return False

    def request(self, method: str, path: str, body=None, timeout: float = 95):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
        try:
            data = None if body is None else json.dumps(body).encode()
            connection.request(method, path, data, {"Content-Type": "application/json", "Connection": "close"})
            response = connection.getresponse()
            content = response.read(12 * 1024 * 1024 + 1)
            require(len(content) <= 12 * 1024 * 1024, "webdriver_response_too_large")
            try:
                value = json.loads(content).get("value")
            except (ValueError, AttributeError):
                raise SmokeFailure("webdriver_invalid_response") from None
            if response.status >= 400 or isinstance(value, dict) and "error" in value:
                code = value.get("error") if isinstance(value, dict) else None
                raise SmokeFailure("webdriver_" + safe_code(code).replace(" ", "_"))
            return value
        finally:
            connection.close()

    def path(self, suffix: str) -> str:
        require(self.session is not None, "webdriver_session_closed")
        return "/session/" + quote(self.session, safe="") + ("/" + suffix if suffix else "")

    def execute(self, script: str, *args):
        return self.request("POST", self.path("execute/sync"), {"script": script, "args": args})

    def invoke(self, command: str, arguments=None, expected_error: str | None = None, timeout: float = 95):
        result = self.request("POST", self.path("execute/async"), {"script": """
            const command = arguments[0], args = arguments[1], done = arguments[arguments.length - 1];
            if (typeof window.__TAURI_INTERNALS__?.invoke !== 'function') {
                done({ok:false, code:'native_bridge_missing'}); return;
            }
            window.__TAURI_INTERNALS__.invoke(command, args).then(
                value => done({ok:true, value}),
                error => done({ok:false, code:typeof error?.code === 'string' ? error.code : 'native_command_failed'})
            );
        """, "args": [command, arguments or {}]}, timeout=timeout)
        require(isinstance(result, dict), "native_command_invalid_result")
        if expected_error:
            require(result.get("ok") is False and result.get("code") == expected_error, "expected_native_error_" + expected_error)
            return None
        require(result.get("ok") is True, "native_" + safe_code(result.get("code")))
        return result.get("value")

    def screenshot(self, output: Path, secrets: set[str]) -> None:
        self.execute("location.hash = '#/settings';")
        wait_until(lambda: self.execute("return !!document.querySelector('.desktop-controls');"), "settings_ui_not_rendered")
        self.execute("document.querySelector('.page-container').scrollTop = 0;")
        self.request("POST", self.path("execute/async"), {"script": "const done=arguments[arguments.length-1]; document.fonts.ready.then(()=>requestAnimationFrame(()=>done(true)));", "args": []})
        require(self.execute("return !document.querySelector('.modal-show') && arguments[0].every(value => !document.body.innerText.includes(value) && !JSON.stringify(localStorage).includes(value));", list(secrets)), "screenshot_would_expose_secret_or_editor")
        raw = self.request("GET", self.path("screenshot"))
        require(isinstance(raw, str), "screenshot_missing")
        try:
            image = base64.b64decode(raw, validate=True)
        except ValueError:
            raise SmokeFailure("screenshot_invalid_encoding") from None
        require(image.startswith(b"\x89PNG\r\n\x1a\n"), "screenshot_invalid_png")
        output.write_bytes(image)

    def close(self) -> None:
        if self.session:
            with contextlib.suppress(Exception):
                self.request("DELETE", self.path(""), timeout=8)
            self.session = None
        self.process.close()


def receive_exact(stream: socket.socket, count: int) -> bytes:
    result = bytearray()
    while len(result) < count:
        chunk = stream.recv(count - len(result))
        if not chunk:
            raise OSError("fixture_peer_closed")
        result.extend(chunk)
    return bytes(result)


class SocksHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        stream = self.request
        stream.settimeout(5)
        stage = "greeting"
        with self.server.lock:
            self.server.accepted += 1
        try:
            version, count = receive_exact(stream, 2)
            methods = receive_exact(stream, count)
            if version != 5 or 0 not in methods:
                return
            stream.sendall(b"\x05\x00")
            stage = "connect_request"
            version, command, reserved, kind = receive_exact(stream, 4)
            if kind == 1:
                host = socket.inet_ntoa(receive_exact(stream, 4))
            elif kind == 4:
                host = socket.inet_ntop(socket.AF_INET6, receive_exact(stream, 16))
            elif kind == 3:
                host = receive_exact(stream, receive_exact(stream, 1)[0]).decode("ascii")
            else:
                return
            port = struct.unpack("!H", receive_exact(stream, 2))[0]
            with self.server.lock:
                self.server.targets.append((host, port))
            if (version, command, reserved, host, port) != (5, 1, 0, TUN_TARGET, 80):
                stream.sendall(b"\x05\x02\x00\x01\x00\x00\x00\x00\x00\x00")
                return
            stream.sendall(b"\x05\x00\x00\x01\x7f\x00\x00\x01\x00\x00")
            stage = "http_request"
            request = bytearray()
            while b"\r\n\r\n" not in request and len(request) <= 8192:
                chunk = stream.recv(1024)
                if not chunk:
                    return
                request.extend(chunk)
            if not request.startswith(b"GET /mihomo-tun-smoke HTTP/1.1\r\n"):
                return
            body = TUN_BODY
            stream.sendall(f"HTTP/1.1 200 OK\r\nContent-Length: {len(body)}\r\nConnection: close\r\n\r\n".encode() + body)
            with self.server.lock:
                self.server.forwarded += 1
        except (OSError, ValueError, UnicodeError) as error:
            with self.server.lock:
                self.server.last_error = socket_diagnostic(stage, error)
            return


class SocksFixture(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = False

    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.targets: list[tuple[str, int]] = []
        self.accepted = 0
        self.forwarded = 0
        self.attempts = 0
        self.last_error = None
        self.last_roundtrip = None
        super().__init__(("127.0.0.1", 0), SocksHandler)
        self.thread = threading.Thread(target=self.serve_forever, daemon=True)
        self.thread.start()

    def roundtrip(self) -> bool:
        # A raw AF_INET socket cannot use HTTP_PROXY, WinInet, or a SOCKS option.
        # Only the /32 route into the real TUN interface can reach this fixture.
        self.attempts += 1
        stage = "connect"
        try:
            with socket.create_connection((TUN_TARGET, 80), timeout=2) as stream:
                stage = "send"
                stream.sendall(f"GET /mihomo-tun-smoke HTTP/1.1\r\nHost: {TUN_TARGET}\r\nConnection: close\r\n\r\n".encode())
                stage = "response_headers"
                response = http.client.HTTPResponse(stream)
                response.begin()
                stage = "response_body"
                body = response.read(4096)
                require(response.status == 200 and body == TUN_BODY, "tun_unexpected_response")
            with self.lock:
                return self.forwarded >= 1 and (TUN_TARGET, 80) in self.targets
        except (OSError, http.client.HTTPException) as error:
            self.last_roundtrip = socket_diagnostic(stage, error)
            return False

    def diagnostics(self) -> dict:
        with self.lock:
            return {"attempts": self.attempts, "accepted": self.accepted,
                    "expectedTargets": sum(target == (TUN_TARGET, 80) for target in self.targets),
                    "unexpectedTargets": sum(target != (TUN_TARGET, 80) for target in self.targets),
                    "forwarded": self.forwarded, "lastSocketError": self.last_roundtrip,
                    "lastFixtureError": self.last_error}

    def close(self) -> None:
        self.shutdown()
        self.server_close()
        self.thread.join(timeout=3)


def socket_diagnostic(stage: str, error: Exception) -> dict:
    # Never retain exception messages: native/HTTP errors can echo request data.
    return {"stage": stage, "type": type(error).__name__,
            "errno": getattr(error, "errno", None), "winerror": getattr(error, "winerror", None)}


def profile(mixed: int, socks: int) -> str:
    return f"""mixed-port: {mixed}
port: 0
socks-port: 0
allow-lan: false
bind-address: 127.0.0.1
mode: rule
log-level: info
ipv6: false
find-process-mode: 'off'
geo-auto-update: false
profile:
  store-selected: false
  store-fake-ip: false
dns:
  enable: true
  ipv6: false
  enhanced-mode: redir-host
  nameserver: [1.1.1.1]
tun:
  enable: false
  device: MihomoSmokeTun
  stack: mixed
  route-address: ['{TUN_TARGET}/32']
  dns-hijack: []
proxies:
  - name: smoke-socks
    type: socks5
    server: 127.0.0.1
    port: {socks}
    udp: false
proxy-groups: []
rules:
  - IP-CIDR,{TUN_TARGET}/32,smoke-socks,no-resolve
  - MATCH,DIRECT
"""


class Acceptance:
    def __init__(self, app: Path, driver: Path, artifacts: Path) -> None:
        self.app, self.driver_path, self.artifacts = app, driver, artifacts
        self.helper = app.with_name("mihomo-desktop-service.exe")
        self.core = app.with_name("mihomo-core.exe")
        self.child_paths = {os.path.normcase(str(path.resolve())) for path in (self.app, self.core, self.helper)}
        for variable in ("ProgramFiles", "ProgramFiles(x86)"):
            if os.environ.get(variable):
                runtime = Path(os.environ[variable]) / "Microsoft" / "EdgeWebView" / "Application"
                for executable in runtime.glob("*/msedgewebview2.exe"):
                    if re.fullmatch(r"\d+\.\d+\.\d+\.\d+", executable.parent.name) and executable.is_file():
                        self.child_paths.add(os.path.normcase(str(executable.resolve())))
        self.directory = Path(os.environ["LOCALAPPDATA"]) / "Mihomo Optimized"
        self.processes = Processes()
        self.wininet = WinInet()
        self.proxy_before = self.wininet.read()
        self.autostart_before = read_autostart()
        self.original_files: dict[Path, bytes | None] = {}
        self.desktop: RestrictedDesktop | None = None
        self.driver: WebViewDriver | None = None
        self.socks: SocksFixture | None = None
        self.gui: ProcessIdentity | None = None
        self.owned_children: dict[int, ProcessIdentity] = {}
        self.ports: set[int] = set()
        self.secrets: set[str] = set()
        self.mutations_started = False
        self.service_owned = False
        self.stage = "preflight"
        self.report = {"passed": False, "platform": "windows", "tests": {}, "diagnostics": []}

    def mark(self, name: str) -> None:
        self.report["tests"][name] = True
        print("PASS: " + name, flush=True)

    def status(self) -> dict:
        value = self.driver.invoke("desktop_status")
        require(isinstance(value, dict), "desktop_status_invalid")
        controller = value.get("controller")
        if isinstance(controller, dict) and isinstance(controller.get("secret"), str):
            self.secrets.add(controller["secret"])
        return value

    def phase(self, wanted: str) -> dict:
        return wait_until(lambda: (status if (status := self.status()).get("phase") == wanted else None), "core_phase_timeout_" + wanted)

    def service_action(self, action: str) -> None:
        # Hosted runners cannot exercise an interactive UAC consent prompt.
        # Only installation/removal is elevated; all operational IPC uses GUI.
        require(action in ("install", "uninstall"), "invalid_supervisor_service_action")
        arguments = [str(self.helper), "--" + action]
        if action == "install":
            arguments += ["--owner-sid", self.desktop.sid]
        result = subprocess.run(arguments, cwd=self.helper.parent, env=child_environment(),
                                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                timeout=65, creationflags=subprocess.CREATE_NO_WINDOW)
        require(result.returncode == 0, "supervisor_service_" + action + "_failed")

    def service_process_access(self) -> dict:
        """Exercise the owner-only query grant without using any mutation right."""
        import pywintypes
        import win32api
        import win32security
        import win32service

        manager = win32service.OpenSCManager(None, None, win32service.SC_MANAGER_CONNECT)
        try:
            service = win32service.OpenService(manager, SERVICE_NAME, win32service.SERVICE_QUERY_STATUS)
            try:
                status = win32service.QueryServiceStatusEx(service)
            finally:
                win32service.CloseServiceHandle(service)
        finally:
            win32service.CloseServiceHandle(manager)
        require(status["CurrentState"] == win32service.SERVICE_RUNNING and status["ProcessId"] > 0,
                "service_identity_not_running")
        pid = status["ProcessId"]
        identity = self.processes.identity(pid)
        require(identity and identity.path == os.path.normcase(str(self.helper.resolve())), "service_process_path_mismatch")
        process = win32api.OpenProcess(0x1000, False, pid)
        try:
            token = win32security.OpenProcessToken(process, win32security.TOKEN_QUERY)
            try:
                user = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
                require(win32security.ConvertSidToStringSid(user) == "S-1-5-18", "service_not_local_system")
            finally:
                token.Close()
        finally:
            process.Close()

        denied = {"terminate": 0x0001, "vmRead": 0x0010, "vmWrite": 0x0020,
                  "duplicateHandle": 0x0040, "writeDacl": 0x00040000}
        win32security.ImpersonateLoggedOnUser(self.desktop.token)
        try:
            # The effective thread token now has the same restrictions as GUI.
            process = win32api.OpenProcess(0x1000, False, pid)
            try:
                require(self.processes.from_handle(int(process), pid) == identity,
                        "service_query_limited_identity_mismatch")
            finally:
                process.Close()
            for name, access in denied.items():
                try:
                    unexpected = win32api.OpenProcess(access, False, pid)
                except pywintypes.error as error:
                    require(error.winerror == 5, "service_access_probe_unexpected_error_" + name)
                else:
                    unexpected.Close()
                    raise SmokeFailure("service_excess_process_access_" + name)
        finally:
            win32security.RevertToSelf()
        return {"serviceAccount": "LocalSystem", "caller": "same_sid_filtered_primary",
                "queryLimitedInformation": True, "deniedRights": list(denied)}

    def managed_core(self, status: dict) -> dict:
        require(status.get("phase") == "running", "core_not_running")
        controller = status.get("controller", {})
        url = urlsplit(controller.get("url", ""))
        secret = controller.get("secret", "")
        require(url.scheme == "http" and url.hostname == "127.0.0.1" and url.port
                and not url.username and not url.password and len(secret) >= 16, "unsafe_managed_controller")
        self.ports.add(url.port)
        self.secrets.add(secret)
        connection = http.client.HTTPConnection("127.0.0.1", url.port, timeout=5)
        try:
            connection.request("GET", "/desktop", headers={"Authorization": "Bearer " + secret, "Connection": "close"})
            response = connection.getresponse()
            content = response.read(64 * 1024)
            require(response.status == 200 and secret.encode() not in content, "managed_identity_request_failed")
            result = json.loads(content)
        finally:
            connection.close()
        require(result.get("managed") is True and result.get("state") == "running", "managed_core_not_ready")
        identity = self.processes.identity(result.get("pid", 0))
        require(identity and identity.path == os.path.normcase(str(self.core.resolve())), "managed_core_process_mismatch")
        self.owned_children[identity.pid] = identity
        return result

    def capture_children(self) -> None:
        roots = [self.gui] if self.gui else []
        if self.driver and self.driver.process.poll() is None:
            roots.append(self.driver.identity)
        for identity in self.processes.descendants(roots, self.child_paths):
            self.owned_children[identity.pid] = identity

    def capture_tun_failure(self, status: dict, core_pid: int) -> None:
        """Keep bounded observations before cleanup removes the failing route."""
        evidence = {"fixture": self.socks.diagnostics(),
                    "host": {"phase": status.get("phase"), "service": status.get("service"), "corePid": core_pid}}
        self.report["tunDiagnostics"] = evidence
        print("TUN fixture: " + json.dumps(evidence["fixture"], sort_keys=True), flush=True)
        # Capture the helper's failure before a successful cleanup overwrites it.
        try:
            native_log = Path(os.environ["PROGRAMDATA"]) / "Mihomo Optimized" / "service" / "firewall-status.json"
            with native_log.open("rb") as log:
                payload = log.read(4097)
            require(len(payload) <= 4096, "diagnostic_firewall_status_too_large")
            native = json.loads(payload)
            require(all(isinstance(native.get(key), str) and re.fullmatch(r"[a-zA-Z0-9_]{1,80}", native[key])
                        for key in ("stage", "code")), "diagnostic_firewall_status_invalid")
            hresult = native.get("hresult")
            require(hresult is None or isinstance(hresult, str) and re.fullmatch(r"0x[0-9a-fA-F]{8}", hresult),
                    "diagnostic_firewall_hresult_invalid")
            evidence["nativeFirewall"] = {"stage": native["stage"], "code": native["code"], "hresult": hresult}
            print("TUN native firewall: " + json.dumps(evidence["nativeFirewall"], sort_keys=True), flush=True)
        except Exception as error:
            evidence["nativeFirewallDiagnosticError"] = type(error).__name__
        controller = status.get("controller") or {}
        try:
            url = urlsplit(controller.get("url", ""))
            require(url.hostname == "127.0.0.1" and url.port, "diagnostic_controller_invalid")
            connection = http.client.HTTPConnection("127.0.0.1", url.port, timeout=3)
            try:
                connection.request("GET", "/configs", headers={"Authorization": "Bearer " + controller["secret"], "Connection": "close"})
                response = connection.getresponse()
                payload = response.read(128 * 1024 + 1)
                require(response.status == 200 and len(payload) <= 128 * 1024, "diagnostic_configs_unavailable")
                config = json.loads(payload)
            finally:
                connection.close()
            # /configs also includes controller settings. Only these fixture
            # fields enter the report; never serialize the complete response.
            fields = ("enable", "stack", "device", "auto-route", "auto-detect-interface",
                      "strict-route", "route-address", "route-exclude-address", "inet4-address",
                      "inet6-address", "dns-hijack")
            tun = config.get("tun", {})
            effective = {"mixedPort": config.get("mixed-port"),
                         "tun": {key: tun[key] for key in fields if key in tun}}
            require(len(json.dumps(effective)) <= 4096, "diagnostic_configs_too_large")
            evidence["effectiveConfig"] = effective
        except Exception as error:
            evidence["configDiagnosticError"] = type(error).__name__

        try:
            evidence["firewallRule"] = owned_firewall_rule(self.core)
            print("TUN firewall rule: " + json.dumps(evidence["firewallRule"], sort_keys=True), flush=True)
        except Exception as error:
            evidence["firewallDiagnosticError"] = safe_code(str(error)) if isinstance(error, SmokeFailure) else type(error).__name__

        try:
            directory = Path(os.environ["PROGRAMDATA"]) / "Mihomo Optimized" / "service" / "logs"
            with (directory / "core.log").open("rb") as log:
                log.seek(0, os.SEEK_END)
                log.seek(max(0, log.tell() - 64 * 1024))
                tail = log.read(64 * 1024).decode("utf-8", errors="replace")
            lines = []
            for line in tail.splitlines():
                if not re.search(r"tun|tcp|smoke-socks|route|error|warn|fatal", line, re.IGNORECASE):
                    continue
                if re.search(r"secret|authorization", line, re.IGNORECASE):
                    continue
                for secret in self.secrets:
                    line = line.replace(secret, "[redacted]")
                line = re.sub(r"\x1b\[[0-9;]*m", "", line)
                line = re.sub(r"[A-Za-z0-9_-]{32,}", "[redacted]", line)
                lines.append(line[:600])
            evidence["coreLogTail"] = lines[-30:]
        except Exception as error:
            evidence["logDiagnosticError"] = type(error).__name__

    def run(self) -> None:
        require(self.core.is_file() and self.helper.is_file(), "installed_sidecars_missing")
        require(not self.processes.at_paths([self.app, self.core, self.helper]), "preexisting_mihomo_process")
        require(not service_installed(), "preexisting_mihomo_service")
        self.directory.mkdir(parents=True, exist_ok=True)
        for name in ("config.yaml", "config.yaml.bak", "preferences.json"):
            path = self.directory / name
            self.original_files[path] = path.read_bytes() if path.exists() else None
        require(not (self.directory / "system-proxy-recovery.json").exists(), "preexisting_proxy_recovery")
        self.mutations_started = True
        (self.directory / "config.yaml").write_text(BAD_CONFIG, encoding="utf-8", newline="")
        (self.directory / "preferences.json").write_text(json.dumps({"desiredRunning": True, "systemProxy": False, "tun": False}), encoding="utf-8")
        self.stage = "non_elevated_gui"
        self.desktop = RestrictedDesktop()
        # Probe before starting fixture threads or child processes. Station
        # changes are synchronous, temporary, and always restored before launch.
        self.report["desktopAccess"] = self.desktop.probe_access(self.artifacts / "desktop-access.json")
        self.socks = SocksFixture()
        mixed = free_port()
        self.ports.add(mixed)
        valid = profile(mixed, self.socks.server_address[1])

        self.driver = WebViewDriver(self.driver_path, self.app, self.processes, self.desktop, self.artifacts)
        gui = wait_until(lambda: self.processes.at_paths([self.app]), "gui_process_not_found")
        require(len(gui) == 1, "multiple_gui_processes")
        self.gui = gui[0]
        self.report["guiToken"] = self.desktop.check_gui(self.gui, self.processes)
        self.mark("non_elevated_gui")
        self.stage = "startup_recovery"
        wait_until(lambda: self.driver.execute("return typeof window.__TAURI_INTERNALS__?.invoke === 'function' && !!document.querySelector('.desktop-statusbar');"), "desktop_ui_not_loaded")
        failed = self.phase("error")
        require(bool(failed.get("lastError")) and failed.get("controller") is None, "startup_failure_not_reported")
        self.driver.execute("location.hash = '#/settings';")
        wait_until(lambda: self.driver.execute("return !!document.querySelector('.desktop-controls .desktop-error');"), "recovery_settings_not_available")
        require(self.driver.execute("const button=[...document.querySelectorAll('.desktop-controls button')].find(item=>/编辑配置文件|Edit config/i.test(item.textContent)); if (!button || button.disabled) return false; button.click(); return true;"), "offline_editor_action_unavailable")
        wait_until(lambda: self.driver.execute("const area=document.querySelector('textarea.config-editor-text'); return !!area && !area.readOnly && area.value === arguments[0];", BAD_CONFIG), "offline_editor_not_editable")
        require(self.driver.execute("const button=[...document.querySelectorAll('.config-editor-footer button')].find(item=>/^(关闭|Close)$/.test(item.textContent.trim())); if (!button) return false; button.click(); return true;"), "offline_editor_close_failed")
        document = self.driver.invoke("desktop_read_config")
        require(document.get("content") == BAD_CONFIG and document.get("writable") is True, "broken_source_cannot_be_read")
        saved = self.driver.invoke("desktop_save_config", {"content": valid, "expectedRevision": document["revision"]})
        require(saved.get("content") == valid and saved.get("revision") != document["revision"], "native_config_repair_failed")
        self.driver.invoke("desktop_reload_config", {"force": True})
        require(self.status().get("phase") == "error", "offline_save_implicitly_started_core")
        self.driver.invoke("desktop_start")
        running = self.phase("running")
        self.managed_core(running)
        self.mark("startup_recovery")

        self.stage = "config_revision"
        self.driver.invoke("desktop_save_config", {"content": "mode: direct\n", "expectedRevision": document["revision"]}, expected_error="config_conflict")
        after = self.driver.invoke("desktop_read_config")
        require(after.get("revision") == saved["revision"] and after.get("content") == valid, "revision_conflict_overwrote_source")
        self.mark("config_revision")

        self.stage = "system_proxy_restore"
        proxy = self.driver.invoke("desktop_set_system_proxy", {"enabled": True})
        require(proxy.get("systemProxy") is True, "system_proxy_not_confirmed")
        actual = self.wininet.read()
        require(actual["flags"] & 2 and actual["server"] == f"127.0.0.1:{mixed}", "system_proxy_not_applied_to_windows")
        self.driver.invoke("desktop_stop")
        self.phase("stopped")
        wait_until(lambda: self.wininet.read() == self.proxy_before, "system_proxy_not_restored_after_stop")
        # Clear the remembered switch so later starts do not silently re-enable it.
        self.driver.invoke("desktop_set_system_proxy", {"enabled": False})
        self.mark("system_proxy_restore")

        self.stage = "autostart_restore"
        require(self.driver.invoke("desktop_set_autostart", {"enabled": True}).get("autostart") is True, "autostart_not_confirmed")
        registered = read_autostart()
        require(registered and registered[0] == f'"{self.app}" --tray', "autostart_registry_mismatch")
        require(self.driver.invoke("desktop_set_autostart", {"enabled": False}).get("autostart") is False, "autostart_not_disabled")
        require(read_autostart() is None, "autostart_registry_not_removed")
        restore_autostart(self.autostart_before)
        require(read_autostart() == self.autostart_before, "autostart_not_restored")
        self.mark("autostart_restore")

        self.stage = "single_instance"
        self.driver.invoke("desktop_start")
        running = self.phase("running")
        identity = self.managed_core(running)
        duplicate = self.desktop.spawn([str(self.app)], self.app.parent)
        try:
            require(duplicate.wait(timeout=12) == 0, "second_instance_exit_failed")
        except subprocess.TimeoutExpired:
            instance = self.processes.identity(duplicate.pid)
            if instance:
                self.processes.kill(instance)
                duplicate.wait(timeout=5)
            raise SmokeFailure("second_instance_did_not_exit") from None
        finally:
            duplicate.close()
        require(self.status().get("controller") == running["controller"], "second_instance_changed_controller")
        require(self.managed_core(self.status()).get("pid") == identity.get("pid"), "second_instance_replaced_core")
        require(len(self.processes.at_paths([self.app])) == 1, "second_gui_survived")
        self.driver.screenshot(self.artifacts / "windows-settings.png", self.secrets)
        self.report["screenshot"] = "windows-settings.png"
        self.mark("single_instance_and_webview2_ui")

        self.stage = "tun_forwarding"
        self.driver.invoke("desktop_stop")
        self.driver.invoke("desktop_set_system_proxy", {"enabled": False})
        self.service_owned = True  # Includes partial installation on a failed IPC.
        tun = {}
        tun_core_pid = 0
        try:
            self.service_action("install")
            installed = self.status()
            require(installed.get("service", {}).get("installed") and service_installed(), "tun_service_not_installed")
            self.stage = "tun_service_identity"
            self.report["serviceProcessAccess"] = self.service_process_access()
            self.report["tunGuiToken"] = self.desktop.check_gui(self.gui, self.processes)
            self.driver.invoke("desktop_set_tun", {"enabled": True})
            self.driver.invoke("desktop_start")
            tun = self.phase("running")
            require(tun.get("tun") is True and tun.get("service", {}).get("running") is True, "tun_service_not_running")
            tun_core_pid = self.managed_core(tun)["pid"]
            self.mark("tun_service_identity")
            self.stage = "tun_forwarding"
            wait_until(self.socks.roundtrip, "tun_forwarding_timeout")
            require(owned_firewall_rule_count(self.core) == 1, "tun_firewall_rule_not_installed")
        except Exception:
            with contextlib.suppress(Exception):
                tun = self.status()
            self.capture_tun_failure(tun, tun_core_pid)
            raise
        with self.socks.lock:
            require(all(target == (TUN_TARGET, 80) for target in self.socks.targets), "tun_fixture_received_unexpected_target")
            self.report["tun"] = {"route": TUN_TARGET + "/32", "socksConnections": len(self.socks.targets), "forwardedRequests": self.socks.forwarded}
        self.mark("tun_forwarding")

        self.stage = "stop_cleanup"
        self.driver.invoke("desktop_stop")
        self.driver.invoke("desktop_set_tun", {"enabled": False})
        self.service_action("uninstall")
        wait_until(lambda: not service_installed(), "tun_service_not_removed")
        require(owned_firewall_rule_count(self.core) == 0, "tun_firewall_rule_not_removed")
        wait_until(lambda: not self.processes.at_paths([self.core]) and all(not listening(port) for port in self.ports), "core_survived_stop")
        require(self.wininet.read() == self.proxy_before, "proxy_changed_during_tun_test")
        self.mark("stop_cleanup")

        self.stage = "crash_cleanup"
        self.driver.invoke("desktop_start")
        self.managed_core(self.phase("running"))
        require(self.driver.invoke("desktop_set_system_proxy", {"enabled": True}).get("systemProxy") is True, "crash_test_proxy_not_enabled")
        require(self.wininet.read()["server"] == f"127.0.0.1:{mixed}", "crash_test_proxy_not_applied")
        self.capture_children()
        self.processes.kill(self.gui)
        # Do not stop the driver/helper or manually restore proxy/core until the
        # actual watchdog and native Job Object have passed these assertions.
        wait_until(lambda: self.wininet.read() == self.proxy_before, "watchdog_did_not_restore_system_proxy")
        wait_until(lambda: not self.processes.at_paths([self.core]) and all(not listening(port) for port in self.ports), "job_did_not_stop_core_after_gui_crash")
        wait_until(lambda: not self.processes.at_paths([self.helper]), "watchdog_process_survived_gui_crash")
        require(not service_installed(), "service_reappeared_after_crash")
        self.mark("crash_cleanup")

    def cleanup(self) -> None:
        if not self.mutations_started:
            return
        errors = []

        def attempt(code: str, operation) -> None:
            try:
                operation()
            except Exception:
                errors.append(code)
                print("CLEANUP error: " + code, flush=True)

        if self.driver:
            attempt("capture_children", self.capture_children)
            if self.gui and self.processes.identity(self.gui.pid) == self.gui:
                # A failed test may still have a responsive host. Let it do the
                # normal restoration before any process-level cleanup fallback.
                with contextlib.suppress(Exception):
                    self.driver.invoke("desktop_stop", timeout=10)
                    self.driver.invoke("desktop_exit", {"force": True}, timeout=10)
            attempt("driver_close", self.driver.close)
        if self.service_owned:
            def uninstall():
                self.service_action("uninstall")
                wait_until(lambda: not service_installed(), "cleanup_service_still_installed", 15)
            attempt("service_uninstall", uninstall)
        for identity in self.processes.at_paths([self.app, self.core, self.helper]):
            attempt("owned_process_termination", lambda identity=identity: self.processes.kill(identity))
        for identity in self.owned_children.values():
            if self.processes.identity(identity.pid) == identity:
                attempt("owned_child_termination", lambda identity=identity: self.processes.kill(identity))
        if self.socks:
            attempt("socks_fixture_close", self.socks.close)
        attempt("proxy_restore", lambda: self.wininet.restore(self.proxy_before))
        attempt("autostart_restore", lambda: restore_autostart(self.autostart_before))
        for path, original in self.original_files.items():
            def restore_file(path=path, original=original):
                if original is None:
                    path.unlink(missing_ok=True)
                else:
                    path.write_bytes(original)
            attempt("source_file_restore", restore_file)
        # This record belongs to this test only: preflight rejected any older one.
        attempt("recovery_record_cleanup", lambda: (self.directory / "system-proxy-recovery.json").unlink(missing_ok=True))
        attempt("proxy_restore_check", lambda: require(self.wininet.read() == self.proxy_before, "proxy_not_restored"))
        attempt("autostart_restore_check", lambda: require(read_autostart() == self.autostart_before, "autostart_not_restored"))
        attempt("process_cleanup_check", lambda: require(not self.processes.at_paths([self.app, self.core, self.helper]), "owned_process_remains"))
        attempt("service_cleanup_check", lambda: require(not service_installed(), "owned_service_remains"))
        if self.service_owned:
            attempt("firewall_cleanup_check", lambda: require(owned_firewall_rule_count(self.core) == 0, "owned_firewall_rule_remains"))
        if self.desktop:
            attempt("restricted_token_close", self.desktop.close)
        self.report["tests"]["final_cleanup"] = not errors
        self.report["diagnostics"].extend({"stage": "cleanup", "code": code} for code in errors)
        print("FAIL: final_cleanup" if errors else "PASS: final_cleanup", flush=True)


def main() -> int:
    acceptance = None
    artifacts = Path("artifacts/verification")
    runner_allowed = sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
    try:
        require(runner_allowed, "disposable_github_windows_runner_required")
        artifacts.mkdir(parents=True, exist_ok=True)
        require(ctypes.windll.shell32.IsUserAnAdmin(), "elevated_runner_required_for_service_acceptance")
        app = Path(os.environ.get("MIHOMO_TEST_APP", "")).resolve(strict=True)
        driver = Path(os.environ.get("MIHOMO_TEST_DRIVER", "")).resolve(strict=True)
        expected = (Path(os.environ["ProgramFiles"]) / "Mihomo Optimized" / "mihomo-optimized.exe").resolve()
        require(app == expected and app.is_file() and driver.is_file(), "installed_application_or_driver_missing")
        acceptance = Acceptance(app, driver, artifacts)
        acceptance.run()
    except Exception as error:
        code = safe_code(str(error)) if isinstance(error, SmokeFailure) else "unexpected_" + type(error).__name__
        print("FAIL: " + code, file=sys.stderr, flush=True)
        if acceptance:
            acceptance.report["diagnostics"].append({"stage": acceptance.stage, "code": code})
        else:
            if runner_allowed:
                (artifacts / "verification.json").write_text(json.dumps({
                    "passed": False, "platform": "windows", "tests": {},
                    "diagnostics": [{"stage": "preflight", "code": code}],
                }, indent=2) + "\n", encoding="utf-8")
            return 1
    finally:
        if acceptance:
            try:
                acceptance.cleanup()
            except Exception as error:
                acceptance.report["diagnostics"].append({"stage": "cleanup", "code": "unexpected_" + type(error).__name__})
            required = {"startup_recovery", "config_revision", "system_proxy_restore", "autostart_restore",
                        "single_instance_and_webview2_ui", "tun_forwarding", "stop_cleanup", "crash_cleanup", "final_cleanup",
                        "non_elevated_gui", "tun_service_identity"}
            acceptance.report["passed"] = all(acceptance.report["tests"].get(name) is True for name in required) and not acceptance.report["diagnostics"]
            (artifacts / "verification.json").write_text(json.dumps(acceptance.report, indent=2) + "\n", encoding="utf-8")
    return 0 if acceptance and acceptance.report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
