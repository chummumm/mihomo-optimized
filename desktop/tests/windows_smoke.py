#!/usr/bin/env python3
"""Acceptance test for the installed Windows application on a disposable runner.

Uses the installed WPF UI, Windows UI Automation, WinInet, pywin32 firewall COM, and a local SOCKS
fixture. No production host or operating-system behavior is mocked.
The explicit test-only named pipe forwards the same real host protocol used by the UI.

setup-windows.ps1 supplies MIHOMO_TEST_APP. This test changes
the runner's own proxy/autostart settings and installs the app's TUN service; it
refuses to run outside a disposable GitHub Actions Windows runner.
An elevated supervisor handles installation/cleanup. The actual native
GUI uses a same-SID filtered primary token at medium integrity; this is not an
interactive UAC-consent test or a separate standard-user-account test.
"""

from __future__ import annotations

import contextlib
import ctypes
from ctypes import wintypes
from dataclasses import dataclass
import hashlib
import http.client
import http.server
import json
import os
from pathlib import Path
import re
import secrets
import socket
import socketserver
import struct
import subprocess
import sys
import threading
import time
from urllib.parse import urlsplit


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
    # application. Windows/.NET still receive their standard runtime paths.
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
        # Only known application/host binaries may enter the cleanup set.
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
            administrator = win32security.CreateWellKnownSid(win32security.WinBuiltinAdministratorsSid, None)
            stage = "create_restricted_token"
            # LUA_TOKEN is 0x4 in winnt.h; pywin32 311 does not export that flag.
            self.token = win32security.CreateRestrictedToken(source,
                win32security.DISABLE_MAX_PRIVILEGE | 0x4, [(administrator, 0)], None, None)
            stage = "set_restricted_default_dacl"
            default_dacl = win32security.GetTokenInformation(self.token, win32security.TokenDefaultDacl)
            require(default_dacl is not None and default_dacl.IsValid(), "restricted_default_dacl_invalid")
            original_ace_count = default_dacl.GetAceCount()
            # A filtered administrator must retain access to its own newly
            # created objects. Preserve every existing ACE and add only TokenUser.
            default_dacl.AddAccessAllowedAceEx(default_dacl.GetAclRevision(), 0, 0x10000000,
                win32security.GetTokenInformation(self.token, win32security.TokenUser)[0])  # GENERIC_ALL
            require(default_dacl.IsValid() and default_dacl.GetAceCount() == original_ace_count + 1,
                    "restricted_default_dacl_merge_invalid")
            win32security.SetTokenInformation(self.token, win32security.TokenDefaultDacl, default_dacl)
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

    def spawn(self, arguments: list[str], cwd: Path, *, suspended: bool = False) -> RestrictedProcess:
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
            if not suspended:
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


class UpdateCacheFixture:
    """Own legacy-shaped files and locks; never invoke an installer or monitor."""

    def __init__(self, directory: Path, app: Path, desktop: RestrictedDesktop) -> None:
        self.directory, self.app, self.desktop = directory, app, desktop
        self.root = directory / "updates"
        self.directories: dict[str, Path] = {}
        self.files: list[Path] = []
        self.expected: dict[Path, str] = {}
        self.preserved: dict[Path, str] = {}
        self.file_lock = None
        self.image_process: RestrictedProcess | None = None
        self.evidence = {"legacyVersion": "0.1.6", "runningImageFixture": "owned_suspended_process"}

    def write(self, path: Path, content: bytes) -> None:
        with path.open("xb") as output:
            self.files.append(path)
            output.write(content)
        self.expected[path] = hashlib.sha256(content).hexdigest()

    def prepare(self) -> None:
        import win32con
        import win32file
        import win32security

        self.root.mkdir(exist_ok=True)
        installer = "mihomo-desktop-0.1.6-windows-x64-setup.exe"
        payloads = {installer: b"legacy installer fixture; never executed\n",
                    installer + ".sigstore.json": b'{"fixture":true}\n',
                    "desktop-update-monitor.exe": b"legacy monitor fixture; never executed\n",
                    "verification.json": b'{"fixture":true}\n'}
        descriptor = win32security.ConvertStringSecurityDescriptorToSecurityDescriptor(
            f"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;{self.desktop.sid})", 1)
        for kind in ("idle", "file_busy", "image_busy", "unknown", "ordinary"):
            name = secrets.token_hex(16)
            if kind == "ordinary":
                name = "user-files-" + name
            path = self.root / name
            path.mkdir()  # Never adopt an existing directory.
            self.directories[kind] = path
            # Match the old updater's per-user directory ACL, including inherited
            # child access for the actual medium-integrity GUI.
            win32security.SetFileSecurity(str(path), win32security.DACL_SECURITY_INFORMATION
                | win32security.PROTECTED_DACL_SECURITY_INFORMATION, descriptor)
            for name, content in payloads.items():
                if kind == "image_busy" and name == "desktop-update-monitor.exe":
                    content = self.app.read_bytes()
                self.write(path / name, content)
            if kind == "unknown":
                self.write(path / "keep-user-file.txt", b"unrelated user content\n")
            if kind in ("unknown", "ordinary"):
                self.preserved.update({p: value for p, value in self.expected.items() if p.parent == path})

        user_file = self.directory / ("update-cleanup-user-" + secrets.token_hex(16) + ".txt")
        self.write(user_file, b"user file outside the update cache\n")
        self.preserved[user_file] = self.expected[user_file]
        backup = self.directory / "config.yaml.bak"
        if not backup.exists():
            self.write(backup, b"# user configuration backup\nmode: rule\n")
        for path in (self.directory / "config.yaml", backup):
            self.preserved[path] = hashlib.sha256(path.read_bytes()).hexdigest()

        self.file_lock = win32file.CreateFile(str(self.directories["file_busy"] / installer),
            win32con.GENERIC_READ, win32con.FILE_SHARE_READ, None, win32con.OPEN_EXISTING,
            win32con.FILE_ATTRIBUTE_NORMAL, None)
        image = self.directories["image_busy"] / "desktop-update-monitor.exe"
        # Windows maps the real installed image before its primary thread runs.
        # Keep that owned process suspended: this checks an active image lock,
        # without entering update handoff, installing anything or opening a GUI.
        self.image_process = self.desktop.spawn([str(image), "--desktop-update-monitor"],
                                                 image.parent, suspended=True)
        require(self.image_process.poll() is None, "update_cache_image_fixture_exited")

    def assert_unchanged(self, snapshot: dict[Path, str], code: str) -> None:
        require(all(path.is_file() and hashlib.sha256(path.read_bytes()).hexdigest() == digest
                    for path, digest in snapshot.items()), code)

    def verify(self) -> None:
        wait_until(lambda: not self.directories["idle"].exists(), "update_cache_legacy_not_removed", 15)
        self.evidence["legacyDirectoryRemoved"] = True
        self.assert_unchanged(self.preserved, "update_cache_user_files_changed")
        busy = {p: digest for p, digest in self.expected.items()
                if p.parent in (self.directories["file_busy"], self.directories["image_busy"])}
        self.assert_unchanged(busy, "update_cache_busy_directory_changed")
        # Observe more than one two-second retry interval while both kinds of
        # occupation remain live. A single pre-sweep observation is insufficient.
        held_until = time.monotonic() + 3

        def held_long_enough():
            require(self.image_process.poll() is None, "update_cache_image_fixture_exited")
            require(all(path.is_file() for path in busy), "update_cache_deleted_occupied_file")
            return time.monotonic() >= held_until

        wait_until(held_long_enough, "update_cache_busy_observation_timeout", 5)
        self.assert_unchanged(busy, "update_cache_busy_directory_changed")
        self.evidence.update({"lockedFilePreserved": True, "running_image_preserved": True})

        released_at = time.monotonic()
        self.file_lock.Close()
        self.file_lock = None
        wait_until(lambda: not self.directories["file_busy"].exists(), "update_cache_file_retry_failed", 15)
        self.evidence.update({"releasedFileRemoved": True,
                              "fileRetrySeconds": round(time.monotonic() - released_at, 3)})
        self.assert_unchanged({p: digest for p, digest in busy.items()
                               if p.parent == self.directories["image_busy"]},
                              "update_cache_live_image_directory_changed")
        require(self.image_process.poll() is None, "update_cache_live_image_terminated")

        released_at = time.monotonic()
        self.image_process.close()  # Only the exact handle returned by our spawn.
        self.image_process = None
        wait_until(lambda: not self.directories["image_busy"].exists(), "update_cache_image_retry_failed", 15)
        self.evidence.update({"releasedImageRemoved": True,
                              "imageRetrySeconds": round(time.monotonic() - released_at, 3)})
        self.assert_unchanged(self.preserved, "update_cache_user_files_changed")
        self.evidence.update({"configurationAndBackupPreserved": True,
                              "unknownDirectoryPreserved": True, "ordinaryDirectoryPreserved": True,
                              "outsideUserFilePreserved": True})

    def close(self) -> None:
        errors = []
        if self.file_lock is not None:
            try:
                self.file_lock.Close()
            except Exception:
                errors.append("file_handle")
            self.file_lock = None
        if self.image_process is not None:
            try:
                self.image_process.close()
            except Exception:
                errors.append("image_process")
            self.image_process = None
        # Delete only files created by this fixture. Never traverse the update
        # root or recursively remove an unknown item, junction or symlink.
        for path in reversed(self.files):
            try:
                path.unlink(missing_ok=True)
            except Exception:
                errors.append("file")
        for path in reversed(list(self.directories.values())):
            try:
                path.rmdir()
            except FileNotFoundError:
                pass
            except Exception:
                errors.append("directory")
        require(not errors, "update_cache_fixture_cleanup_failed")


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


class NativeDriver:
    """Exercise the installed WPF UI and its real host through an opt-in local pipe.

    No TCP listener or browser is involved. The pipe belongs to the same user,
    uses a fresh unpredictable name, and only exists on this explicit test launch.
    UI assertions use Windows UI Automation independently of the host protocol.
    """
    def __init__(self, app: Path, processes: Processes, desktop: RestrictedDesktop,
                 artifacts: Path) -> None:
        self.pipe = None
        self.buffer = bytearray()
        self.next_id = 0
        self.pipe_name = "mihomo-acceptance-" + secrets.token_hex(20)
        self.process = desktop.spawn([str(app), "--automation-pipe", self.pipe_name], app.parent)
        self.identity = processes.identity(self.process.pid)
        try:
            require(self.identity and self.identity.path == os.path.normcase(str(app.resolve())),
                    "native_ui_process_mismatch")
            wait_until(self.connect, "native_ui_pipe_start_timeout", 30)
            wait_until(lambda: self.snapshot().get("ready") is True, "native_ui_not_ready", 20)
            evidence = {"ready": True, "framework": "WPF", "browserRuntime": False,
                        "requestedDesktop": r"winsta0\default",
                        "requestedToken": desktop.check_token(desktop.token)}
            (artifacts / "native-startup.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
        except BaseException:
            self.close()
            raise

    def connect(self) -> bool:
        import pywintypes
        import win32con
        import win32file
        if self.process.poll() is not None:
            raise SmokeFailure("native_ui_exited_before_pipe_ready")
        if self.pipe is not None:
            return True
        try:
            self.pipe = win32file.CreateFile("\\\\.\\pipe\\" + self.pipe_name,
                win32con.GENERIC_READ | win32con.GENERIC_WRITE, 0, None,
                # SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION: the medium
                # GUI may identify the test account, never borrow its elevation.
                win32con.OPEN_EXISTING, 0x00100000 | 0x00010000, None)
            return True
        except pywintypes.error as error:
            if error.winerror not in (2, 231):
                raise SmokeFailure("native_pipe_connect_winerror_" + str(error.winerror)) from None
            return False

    def invoke(self, command: str, arguments=None, expected_error=None, timeout: float = 95):
        import win32file
        import win32pipe
        require(self.pipe is not None, "native_pipe_closed")
        self.next_id += 1
        request_id = self.next_id
        payload = json.dumps({"id": request_id, "method": command, "params": arguments or {}},
                             ensure_ascii=False).encode("utf-8") + b"\n"
        require(len(payload) <= 8 * 1024 * 1024, "native_request_too_large")
        win32file.WriteFile(self.pipe, payload)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            while b"\n" in self.buffer:
                line, _, remaining = self.buffer.partition(b"\n")
                self.buffer = bytearray(remaining)
                require(len(line) <= 12 * 1024 * 1024, "native_response_too_large")
                result = json.loads(line)
                if "event" in result:
                    continue
                require(result.get("id") == request_id, "native_response_id_mismatch")
                if expected_error:
                    allowed = (expected_error,) if isinstance(expected_error, str) else expected_error
                    require(result.get("error", {}).get("code") in allowed,
                            "expected_native_error_" + allowed[0])
                    return None
                require("error" not in result,
                        "native_" + safe_code(result.get("error", {}).get("code")))
                require("result" in result, "native_response_result_missing")
                return result["result"]
            available = win32pipe.PeekNamedPipe(self.pipe, 0)[1]
            if available:
                _, chunk = win32file.ReadFile(self.pipe, min(available, 65536))
                self.buffer.extend(chunk)
                require(len(self.buffer) <= 12 * 1024 * 1024, "native_response_too_large")
            else:
                time.sleep(0.02)
        raise SmokeFailure("native_command_timeout_" + safe_code(command))

    def snapshot(self) -> dict:
        result = self.invoke("native_snapshot", timeout=5)
        require(isinstance(result, dict), "native_ui_snapshot_invalid")
        return result

    def navigate(self, page: str) -> None:
        self.invoke("native_navigate", {"page": page}, timeout=10)
        wait_until(lambda: self.snapshot().get("page") == page, "native_navigation_failed", 10)

    def ui(self, automation_id: str, action: str = "read") -> dict:
        script = Path(__file__).with_name("uia-query.ps1").resolve()
        result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", str(script), "-ApplicationProcessId", str(self.identity.pid),
            "-AutomationId", automation_id, "-Action", action],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15,
            creationflags=subprocess.CREATE_NO_WINDOW)
        require(result.returncode == 0, "native_uia_query_failed_" + safe_code(automation_id))
        return json.loads(result.stdout.lstrip("\ufeff"))

    def screenshot(self, output: Path, secrets: set[str]) -> None:
        self.navigate("settings")
        visible = self.ui("*")
        require(visible.get("found") is True, "native_settings_ui_not_found")
        text = json.dumps(visible, ensure_ascii=False)
        require(all(value not in text for value in secrets), "screenshot_would_expose_secret_or_editor")
        self.invoke("native_screenshot", {"path": str(output.resolve())}, timeout=10)
        require(output.is_file() and output.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"),
                "native_screenshot_invalid")

    def close(self) -> None:
        if self.pipe is not None:
            self.pipe.Close()
            self.pipe = None
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


class ResourceFixture(http.server.ThreadingHTTPServer):
    """Finite loopback delays exercise the real host/core budget and cache paths."""

    daemon_threads = True

    def __init__(self, proxy_check=None, *, hold_success: bool = False) -> None:
        super().__init__(("127.0.0.1", 0), ResourceHandler)
        self.delay = float(os.environ.get("MIHOMO_TEST_RESOURCE_DELAY_SECONDS", "22"))
        require(0 < self.delay <= 30, "resource_fixture_delay_invalid")
        self.token = secrets.token_hex(24)
        self.cache_name = "desktop-bootstrap-" + secrets.token_hex(8)
        self.requests: dict[str, int] = {}
        self.lock = threading.Lock()
        self.stopping = threading.Event()
        self.offline = False
        self.proxy_check = proxy_check
        self.pending_proxy_checks = 0
        self.pending_proxy_failure = None
        # First-launch UI inspection must happen while the resource is missing,
        # even if initializing the native WPF runtime is unusually slow.
        self.hold_success = hold_success
        self.release_success = threading.Event()
        if not hold_success:
            self.release_success.set()
        self.thread = threading.Thread(target=self.serve_forever, daemon=True)
        self.thread.start()

    def count(self, path: str) -> int:
        with self.lock:
            return self.requests.get(path, 0)

    def check_pending_proxy(self) -> None:
        if self.proxy_check is None:
            return
        try:
            disabled = self.proxy_check()
            failure = None if disabled else "system_proxy_enabled_before_resource_ready"
        except Exception:
            failure = "pending_system_proxy_query_failed"
        with self.lock:
            self.pending_proxy_checks += 1
            self.pending_proxy_failure = self.pending_proxy_failure or failure

    def require_direct_preparation(self, previous_checks: int) -> None:
        with self.lock:
            require(self.pending_proxy_checks > previous_checks, "resource_proxy_observation_missing")
            require(self.pending_proxy_failure is None, self.pending_proxy_failure or "resource_proxy_observation_failed")

    def configuration(self, base: str, *, failing: bool = False) -> str:
        endpoint = "/timeout.yaml" if failing else "/rules.yaml"
        cache = self.cache_name + ("-timeout" if failing else "")
        # A smaller configured timeout tests expiry without waiting 180 seconds.
        timeout = 1 if failing else max(int(self.delay) + 15, 55 if self.hold_success else 0)
        document = base.replace("rules:\n", "rules:\n  - RULE-SET,desktop-bootstrap,DIRECT\n", 1)
        return document + f"""resource-bootstrap:
  timeout: {timeout}
rule-providers:
  desktop-bootstrap:
    type: http
    behavior: domain
    format: yaml
    url: http://127.0.0.1:{self.server_port}{endpoint}?token={self.token}
    path: ./rules/{cache}.yaml
    proxy: DIRECT
    interval: 0
"""

    def close(self) -> None:
        self.stopping.set()
        self.release_success.set()
        self.shutdown()
        self.server_close()
        self.thread.join(timeout=3)


class ResourceHandler(http.server.BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        fixture = self.server
        path = urlsplit(self.path).path
        with fixture.lock:
            fixture.requests[path] = fixture.requests.get(path, 0) + 1
        if fixture.offline or path not in ("/rules.yaml", "/timeout.yaml"):
            code, body = 503, b"fixture offline\n"
        else:
            started = time.monotonic()
            deadline = started + (5 if path == "/timeout.yaml" else fixture.delay)
            hold_expired = False
            while time.monotonic() < deadline or (path == "/rules.yaml" and not fixture.release_success.is_set()):
                # The successful request is still withholding the required file,
                # so the new core cannot be ready. Timeout requests may already
                # have been cancelled by the core and are deliberately excluded.
                if path == "/rules.yaml":
                    fixture.check_pending_proxy()
                    if not fixture.release_success.is_set() and time.monotonic() - started >= 45:
                        hold_expired = True
                        break
                if fixture.stopping.wait(0.1):
                    break
            code, body = ((503, b"fixture inspection deadline exceeded\n") if hold_expired else
                          (200, b"payload:\n  - '+.bootstrap-fixture.invalid'\n"))
        with contextlib.suppress(BrokenPipeError, ConnectionResetError, OSError):
            self.send_response(code)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(body)

    def log_message(self, _format: str, *args: object) -> None:
        pass


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
    def __init__(self, app: Path, artifacts: Path) -> None:
        self.app, self.artifacts = app, artifacts
        self.helper = app.with_name("mihomo-desktop-service.exe")
        self.core = app.with_name("mihomo-core.exe")
        self.host = app.with_name("mihomo-desktop-host.exe")
        self.child_paths = {os.path.normcase(str(path.resolve())) for path in
                            (self.app, self.host, self.core, self.helper)}
        self.directory = Path(os.environ["LOCALAPPDATA"]) / "Mihomo Optimized"
        self.processes = Processes()
        self.wininet = WinInet()
        self.proxy_before = self.wininet.read()
        self.proxy_disabled: dict | None = None
        self.autostart_before = read_autostart()
        self.original_files: dict[Path, bytes | None] = {}
        self.desktop: RestrictedDesktop | None = None
        self.driver: NativeDriver | None = None
        self.socks: SocksFixture | None = None
        self.resources: ResourceFixture | None = None
        self.update_cache: UpdateCacheFixture | None = None
        self.gui: ProcessIdentity | None = None
        self.service_identity: ProcessIdentity | None = None
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

    def save_configuration(self, content: str) -> None:
        current = self.driver.invoke("desktop_read_config")
        self.driver.invoke("desktop_save_config", {"content": content, "expectedRevision": current["revision"]})

    def launch_gui(self, artifacts: Path) -> dict:
        require(self.driver is None and self.gui is None, "previous_gui_session_not_closed")
        artifacts.mkdir(parents=True, exist_ok=True)
        self.driver = NativeDriver(self.app, self.processes, self.desktop, artifacts)
        gui = wait_until(lambda: self.processes.at_paths([self.app]), "gui_process_not_found")
        require(len(gui) == 1, "multiple_gui_processes")
        self.gui = gui[0]
        return self.desktop.check_gui(self.gui, self.processes)

    def check_automatic_startup(self, base: str) -> None:
        """The first GUI launch must become ready without a start/restart command."""
        self.stage = "automatic_startup_ready"
        self.resources = ResourceFixture(lambda: self.wininet.read() == self.proxy_disabled, hold_success=True)
        self.secrets.add(self.resources.token)
        cache = self.directory / "runtime" / "rules" / (self.resources.cache_name + ".yaml")
        require(not cache.exists(), "automatic_startup_cache_already_exists")
        (self.directory / "config.yaml").write_text(self.resources.configuration(base), encoding="utf-8", newline="")
        (self.directory / "preferences.json").write_text(
            json.dumps({"desiredRunning": True, "systemProxy": False, "tun": False}), encoding="utf-8")
        self.update_cache = UpdateCacheFixture(self.directory, self.app, self.desktop)
        self.report["updateCacheCleanup"] = self.update_cache.evidence
        self.update_cache.prepare()
        evidence = {"desiredRunning": True, "coldResourceCache": True,
                    "resourceDelaySeconds": self.resources.delay, "statusResponseMilliseconds": []}
        self.report["automaticStartup"] = evidence
        started = time.monotonic()
        self.report["guiToken"] = self.launch_gui(self.artifacts / "automatic-startup")
        self.mark("non_elevated_gui")
        wait_until(lambda: self.driver.snapshot().get("ready") is True,
                   "automatic_startup_ui_not_loaded", 15)
        wait_until(lambda: self.resources.count("/rules.yaml") > 0, "automatic_startup_resource_not_requested", 15)
        require(not cache.exists(), "automatic_startup_resource_released_before_inspection")

        for _ in range(3):
            read_at = time.monotonic()
            try:
                preparing = self.status(timeout=3)
            except (OSError, http.client.HTTPException):
                raise SmokeFailure("automatic_startup_status_unresponsive") from None
            elapsed = time.monotonic() - read_at
            evidence["statusResponseMilliseconds"].append(round(elapsed * 1000, 1))
            require(elapsed < 2, "automatic_startup_status_blocked_by_preparation")
            require(preparing.get("phase") == "starting" and preparing.get("lastError") is None
                    and preparing.get("controller") is None and preparing.get("systemProxy") is False,
                    "automatic_startup_preparation_status_invalid")
            require(self.wininet.read() == self.proxy_disabled, "automatic_startup_enabled_system_proxy_early")

        self.driver.navigate("proxies")
        wait_until(lambda: self.driver.snapshot().get("phase") == "starting",
                   "automatic_startup_preparing_ui_missing", 8)
        require(not self.driver.snapshot().get("error"), "automatic_startup_ui_reported_failure")
        visible = self.driver.ui("*")
        preparing_text = "\n".join(item.get("name", "") for item in visible.get("elements", []))
        require(visible.get("found") and "正在准备启动资源" in preparing_text,
                "automatic_startup_proxy_preparation_message_missing")
        require(not re.search(r"请先.*启动内核|内核尚未运行|没有代理策略组|导入订阅", preparing_text),
                "automatic_startup_proxy_stopped_hint_visible")
        self.driver.navigate("home")
        start_button = self.driver.ui("StartCore")
        restart_button = self.driver.ui("RestartCore")
        require(start_button.get("found") and not start_button.get("enabled")
                and restart_button.get("found") and not restart_button.get("enabled"),
                "automatic_startup_actions_enabled_during_preparation")
        phase_control = self.driver.ui("CorePhase")
        require(phase_control.get("found") and re.search(r"正在启动|Starting", phase_control.get("name", ""), re.I),
                "automatic_startup_native_phase_missing")
        self.driver.navigate("settings")
        wait_until(lambda: self.driver.snapshot().get("phase") == "starting",
                   "automatic_startup_settings_not_responsive", 8)
        require(not self.driver.snapshot().get("error"), "automatic_startup_settings_reported_failure")
        preparing = self.status(timeout=3)
        require(preparing.get("phase") == "starting" and preparing.get("lastError") is None
                and preparing.get("controller") is None and not cache.exists(),
                "automatic_startup_inspection_missed_preparation")
        evidence.update({"preparationPhase": "starting", "preparationErrorAbsent": True,
                         "preparationControllerAbsent": True, "startAndRestartDisabled": True,
                         "preparationUiWithoutFailureHint": True})
        self.resources.release_success.set()

        def automatically_ready():
            observed = self.status(timeout=3)
            require(observed.get("phase") != "error" and observed.get("lastError") is None,
                    "automatic_startup_failed_without_manual_action")
            return observed if observed.get("phase") == "running" else None

        running = wait_until(automatically_ready, "automatic_startup_did_not_become_ready", self.resources.delay + 20)
        initial_core = self.managed_core(running)
        require(cache.is_file() and b"bootstrap-fixture.invalid" in cache.read_bytes(), "automatic_startup_cache_not_persisted")
        require(running.get("systemProxy") is False and self.wininet.read() == self.proxy_disabled,
                "automatic_startup_changed_system_proxy")
        self.resources.require_direct_preparation(0)
        wait_until(lambda: self.driver.snapshot().get("phase") == "running",
                   "automatic_startup_running_ui_missing", 8)
        evidence.update({"readyWithoutStartOrRestart": True, "cachePersisted": True,
                         "startupSeconds": round(time.monotonic() - started, 3),
                         "preparationProxyChecks": self.resources.pending_proxy_checks})

        # Cleanup is independent of core startup. Inspect it only after Ready so
        # occupied-file retry waits cannot consume the held cold-resource window.
        self.stage = "update_cache_cleanup"
        self.update_cache.verify()
        require(self.processes.identity(self.gui.pid) == self.gui, "update_cache_gui_was_restarted")
        after_cleanup = self.status(timeout=3)
        require(after_cleanup.get("phase") == "running" and after_cleanup.get("lastError") is None
                and self.managed_core(after_cleanup)["pid"] == initial_core["pid"],
                "update_cache_cleanup_changed_running_core")
        preferences = json.loads((self.directory / "preferences.json").read_text(encoding="utf-8"))
        require(preferences.get("desiredRunning") is True and preferences.get("systemProxy") is False
                and preferences.get("tun") is False, "update_cache_cleanup_changed_preferences")
        self.update_cache.evidence.update({"retryWithoutRestart": True, "coreStillRunning": True,
                                          "preferencesPreserved": True})
        self.mark("update_cache_cleanup")
        self.stage = "automatic_startup_ready"

        # Close this owned GUI normally before launching the original bad-YAML
        # recovery case. A second-instance forward must not mask that negative test.
        self.driver.invoke("desktop_stop")
        self.capture_children()
        first_children = list(self.owned_children.values())
        # Exercise the actual elevated installer's --quit path against the
        # ordinary UI, including both sides' pipe/process identity checks.
        quit_result = subprocess.run([str(self.app), "--quit"], cwd=self.app.parent,
                                     timeout=45, creationflags=subprocess.CREATE_NO_WINDOW)
        require(quit_result.returncode == 0, "elevated_installer_quit_failed")
        evidence["elevatedInstallerQuit"] = True
        wait_until(lambda: not self.processes.at_paths([self.app, self.host, self.core, self.helper]),
                   "automatic_startup_gui_did_not_exit", 20)
        self.driver.close()
        self.driver, self.gui = None, None
        wait_until(lambda: all(self.processes.identity(identity.pid) != identity for identity in first_children),
                   "automatic_startup_native_children_survived", 15)
        require(all(not listening(port) for port in self.ports), "automatic_startup_ports_survived_exit")
        self.update_cache.close()
        self.update_cache = None
        cache.unlink()
        self.resources.close()
        self.resources = None
        self.mark("automatic_startup_ready")

    def check_resource_bootstrap(self, base: str) -> str:
        self.stage = "resource_bootstrap"
        self.driver.invoke("desktop_stop")
        require(self.wininet.read() == self.proxy_disabled, "cold_start_proxy_not_disabled")
        self.resources = ResourceFixture(lambda: self.wininet.read() == self.proxy_disabled)
        self.secrets.add(self.resources.token)
        source = self.resources.configuration(base)
        self.save_configuration(source)
        cache = self.directory / "runtime" / "rules" / (self.resources.cache_name + ".yaml")
        require(not cache.exists(), "cold_user_resource_cache_already_exists")
        started = time.monotonic()
        self.driver.invoke("desktop_start")
        elapsed = time.monotonic() - started
        running = self.phase("running")
        identity = self.managed_core(running)
        require(elapsed >= self.resources.delay * 0.9 and self.resources.count("/rules.yaml") >= 1,
                "delayed_resource_not_awaited")
        self.resources.require_direct_preparation(0)
        require(running.get("systemProxy") is False and self.wininet.read() == self.proxy_disabled,
                "cold_start_changed_system_proxy")
        require(cache.is_file() and b"bootstrap-fixture.invalid" in cache.read_bytes(), "resource_cache_not_persisted")
        self.save_configuration(self.resources.configuration(base, failing=True))
        failed_at = time.monotonic()
        self.driver.invoke("desktop_reload_config", {"force": True},
                           expected_error=("resource_bootstrap_timeout", "reload_invalid", "reload_failed"))
        failure_seconds = time.monotonic() - failed_at
        # Background native status polling must not erase this actionable error.
        time.sleep(2.2)
        after = self.status()
        require(after.get("phase") == "running" and after.get("controller") == running.get("controller")
                and self.managed_core(after)["pid"] == identity["pid"], "resource_failure_did_not_restore_active_core")
        require(failure_seconds < 10 and self.resources.count("/timeout.yaml") >= 1,
                "short_resource_budget_not_enforced")
        message = after.get("lastError", "")
        require(isinstance(message, str) and message and self.resources.token not in message
                and "http://" not in message and len(message) < 1024, "resource_error_diagnostic_unsafe")
        self.save_configuration(source)
        self.driver.invoke("desktop_reload_config", {"force": True})
        require(self.status().get("lastError") is None, "successful_reload_kept_resource_error")
        self.resources.offline = True
        self.report["resourceBootstrap"] = {"delaySeconds": self.resources.delay,
                                             "startupSeconds": round(elapsed, 3),
                                             "failedReloadSeconds": round(failure_seconds, 3),
                                             "cachePersisted": True, "oldCoreRestored": True,
                                             "coldUserStartWithoutSystemProxy": True,
                                             "userPreparationProxyChecks": self.resources.pending_proxy_checks}
        self.mark("resource_bootstrap")
        return source

    def status(self, timeout: float = 95) -> dict:
        value = self.driver.invoke("desktop_status", timeout=timeout)
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
        self.service_identity = identity
        return {"serviceAccount": "LocalSystem", "caller": "same_sid_filtered_primary",
                "queryLimitedInformation": True, "deniedRights": list(denied)}

    def check_service_proxy_recovery(self, running: dict, mixed: int) -> None:
        """Briefly deny identity queries to this fixture's service, then restore.

        The retained, identity-checked handle restores the exact original DACL in
        finally. No process is replaced, no production identity check is bypassed,
        and each observation is bounded well inside the normal 20s service lease.
        """
        import win32api
        import win32security

        self.stage = "system_proxy_service_recovery"
        identity = self.service_identity
        require(identity is not None and self.processes.identity(identity.pid) == identity,
                "proxy_recovery_service_identity_changed")
        current = self.driver.invoke("desktop_set_system_proxy", {"enabled": True})
        require(current.get("systemProxy") is True, "service_recovery_proxy_not_enabled")
        core_pid = self.managed_core(running)["pid"]
        # READ_CONTROL | WRITE_DAC | PROCESS_QUERY_LIMITED_INFORMATION. Rights
        # on this held handle survive the temporary deny ACE for new opens.
        process = win32api.OpenProcess(0x00061000, False, identity.pid)
        try:
            require(self.processes.from_handle(int(process), identity.pid) == identity,
                    "proxy_recovery_service_handle_mismatch")
            token = win32security.OpenProcessToken(process, win32security.TOKEN_QUERY)
            try:
                user = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
                require(win32security.ConvertSidToStringSid(user) == "S-1-5-18",
                        "proxy_recovery_service_not_system")
            finally:
                token.Close()
            flags = win32security.DACL_SECURITY_INFORMATION
            descriptor = win32security.GetSecurityInfo(process, win32security.SE_KERNEL_OBJECT, flags)
            original = descriptor.GetSecurityDescriptorDacl()
            require(original is not None and original.IsValid(), "service_original_dacl_invalid")
            original_sddl = win32security.ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, flags)
            changed = win32security.GetSecurityInfo(process, win32security.SE_KERNEL_OBJECT, flags).GetSecurityDescriptorDacl()
            require(changed is not None and changed.IsValid(), "service_probe_dacl_invalid")
            changed.SetEntriesInAcl([{
                "AccessPermissions": 0x1000, "AccessMode": win32security.DENY_ACCESS,
                "Inheritance": 0,
                "Trustee": {"TrusteeForm": win32security.TRUSTEE_IS_SID,
                            "TrusteeType": win32security.TRUSTEE_IS_USER,
                            "Identifier": win32security.ConvertStringSidToSid(self.desktop.sid)},
            }])
            require(changed.IsValid(), "service_probe_dacl_merge_failed")
            started = time.monotonic()
            try:
                win32security.SetSecurityInfo(process, win32security.SE_KERNEL_OBJECT, flags,
                                              None, None, changed, None)

                def identity_rejected():
                    observed = self.status(timeout=2)
                    return observed if observed.get("phase") == "error" else None

                failed = wait_until(identity_rejected, "service_identity_outage_not_observed", 6)
                require("无法验证服务进程" in (failed.get("lastError") or "")
                        and "系统错误：5" in (failed.get("lastError") or "")
                        and failed.get("controller") is None and failed.get("systemProxy") is False,
                        "service_identity_outage_not_safely_reported")
                require(self.wininet.read() == self.proxy_disabled,
                        "service_identity_outage_did_not_restore_proxy")
            finally:
                win32security.SetSecurityInfo(process, win32security.SE_KERNEL_OBJECT, flags,
                                              None, None, original, None)
            denied_seconds = time.monotonic() - started
            require(denied_seconds < 10, "service_identity_probe_exceeded_lease_margin")
            restored = win32security.GetSecurityInfo(process, win32security.SE_KERNEL_OBJECT, flags)
            require(win32security.ConvertSecurityDescriptorToStringSecurityDescriptor(restored, 1, flags) == original_sddl,
                    "service_identity_dacl_not_restored")
        finally:
            process.Close()

        def proxy_restored():
            observed = self.status(timeout=3)
            return observed if observed.get("phase") == "running" and observed.get("systemProxy") is True else None

        recovered = wait_until(proxy_restored, "confirmed_service_did_not_restore_proxy", 8)
        require(self.managed_core(recovered)["pid"] == core_pid
                and self.wininet.read()["server"] == f"127.0.0.1:{mixed}",
                "service_proxy_recovery_changed_core_or_port")
        self.report["systemProxyRecovery"] = {"identityQueryDeniedSeconds": round(denied_seconds, 3),
                                              "originalDaclRestored": True, "sameCorePid": True,
                                              "proxyRestoredAfterConfirmation": True}
        self.driver.invoke("desktop_set_system_proxy", {"enabled": False})
        require(self.wininet.read() == self.proxy_disabled, "service_proxy_recovery_cleanup_failed")
        self.mark("system_proxy_service_recovery")

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
        require(not self.processes.at_paths([self.app, self.host, self.core, self.helper]), "preexisting_mihomo_process")
        require(not service_installed(), "preexisting_mihomo_service")
        before_proxy = self.wininet.read()
        source = self.directory / "config.yaml"
        before_source = source.read_bytes() if source.exists() else None
        rejected = subprocess.run([str(self.host), "--stdio", "--parent-pid", str(os.getpid())],
            input=b"", stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10,
            env=child_environment(), creationflags=subprocess.CREATE_NO_WINDOW)
        require(rejected.returncode != 0 and b"desktop_parent_invalid" in rejected.stderr,
                "native_host_accepted_unrelated_parent")
        require(self.wininet.read() == before_proxy
                and (source.read_bytes() if source.exists() else None) == before_source,
                "native_parent_rejection_changed_user_state")
        self.mark("native_host_parent_binding")
        self.directory.mkdir(parents=True, exist_ok=True)
        for name in ("config.yaml", "config.yaml.bak", "preferences.json"):
            path = self.directory / name
            self.original_files[path] = path.read_bytes() if path.exists() else None
        require(not (self.directory / "system-proxy-recovery.json").exists(), "preexisting_proxy_recovery")
        self.mutations_started = True
        # Make the negative prerequisite explicit, including PAC/autodetection.
        # The runner's original per-user settings are restored in cleanup.
        self.wininet.restore({"flags": 1, "server": "", "bypass": "", "pac": ""})
        self.proxy_disabled = self.wininet.read()
        require(self.proxy_disabled["flags"] == 1 and not self.proxy_disabled["server"]
                and not self.proxy_disabled["pac"], "wininet_direct_fixture_not_applied")
        self.socks = SocksFixture()
        mixed = free_port()
        self.ports.add(mixed)
        valid = profile(mixed, self.socks.server_address[1])

        self.stage = "non_elevated_gui"
        self.desktop = RestrictedDesktop()
        self.check_automatic_startup(valid)
        (self.directory / "config.yaml").write_text(BAD_CONFIG, encoding="utf-8", newline="")
        (self.directory / "preferences.json").write_text(json.dumps({"desiredRunning": True, "systemProxy": False, "tun": False}), encoding="utf-8")
        self.stage = "startup_recovery"
        self.report["recoveryGuiToken"] = self.launch_gui(self.artifacts)
        wait_until(lambda: self.driver.snapshot().get("ready") is True, "desktop_ui_not_loaded")
        failed = self.phase("error")
        require(bool(failed.get("lastError")) and failed.get("controller") is None, "startup_failure_not_reported")
        self.driver.navigate("profiles")
        wait_until(lambda: bool(self.driver.snapshot().get("error")), "recovery_error_not_available")
        edit = self.driver.ui("EditConfig")
        require(edit.get("found") and edit.get("enabled"), "offline_editor_action_unavailable")
        require(self.driver.ui("EditConfig", "invoke").get("invoked"), "offline_editor_open_failed")
        def editable_broken_configuration():
            area = self.driver.ui("ConfigEditor")
            return (area.get("found") and area.get("readOnly") is False
                    and area.get("value", "").replace("\r\n", "\n") == BAD_CONFIG)
        wait_until(editable_broken_configuration, "offline_editor_not_editable")
        require(self.driver.ui("CloseEditor", "invoke").get("invoked"), "offline_editor_close_failed")
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

        base = valid
        valid = self.check_resource_bootstrap(base)

        self.stage = "system_proxy_restore"
        proxy = self.driver.invoke("desktop_set_system_proxy", {"enabled": True})
        require(proxy.get("systemProxy") is True, "system_proxy_not_confirmed")
        actual = self.wininet.read()
        require(actual["flags"] & 2 and actual["server"] == f"127.0.0.1:{mixed}", "system_proxy_not_applied_to_windows")
        self.driver.invoke("desktop_stop")
        self.phase("stopped")
        wait_until(lambda: self.wininet.read() == self.proxy_disabled, "system_proxy_not_restored_after_stop")
        require(json.loads((self.directory / "preferences.json").read_text(encoding="utf-8"))["systemProxy"] is True,
                "stop_did_not_preserve_system_proxy_preference")
        # A remembered proxy preference must not point Windows at a core that is
        # still preparing resources, including a failed cold start and retry.
        self.resources.offline = False
        self.save_configuration(self.resources.configuration(base, failing=True))
        self.driver.invoke("desktop_start", expected_error=("resource_bootstrap_timeout", "reload_invalid", "core_start_failed"))
        failed = self.phase("error")
        require(failed.get("controller") is None and failed.get("systemProxy") is False
                and self.wininet.read() == self.proxy_disabled,
                "failed_start_enabled_system_proxy")
        self.save_configuration(valid)
        cache = self.directory / "runtime" / "rules" / (self.resources.cache_name + ".yaml")
        require(cache.is_file(), "preferred_proxy_cache_fixture_missing")
        cache.unlink()  # Only this run's random, previously verified cache.
        previous_checks = self.resources.pending_proxy_checks
        previous_delay = self.resources.delay
        self.resources.delay = min(previous_delay, 1.0)
        try:
            self.driver.invoke("desktop_start")
        finally:
            self.resources.delay = previous_delay
        ready = self.phase("running")
        self.managed_core(ready)
        self.resources.require_direct_preparation(previous_checks)
        require(ready.get("systemProxy") is True and cache.is_file()
                and self.wininet.read()["server"] == f"127.0.0.1:{mixed}",
                "preferred_proxy_not_restored_after_cold_start")
        self.report["systemProxyStartup"] = {"wininetDirectBeforeStart": True,
                                               "failedStartKeptDirect": True,
                                               "coldRetryKeptDirectWhilePreparing": True,
                                               "preferredProxyAppliedAfterReady": True,
                                               "preparationProxyChecks": self.resources.pending_proxy_checks - previous_checks}
        self.driver.invoke("desktop_stop")
        require(self.wininet.read() == self.proxy_disabled, "preferred_proxy_not_restored_after_stop")
        self.resources.offline = True
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
        cache = self.directory / "runtime" / "rules" / (self.resources.cache_name + ".yaml")
        require(cache.is_file() and b"bootstrap-fixture.invalid" in cache.read_bytes(), "offline_start_damaged_resource_cache")
        self.report["resourceBootstrap"]["offlineCacheStart"] = True
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
        self.mark("single_instance_and_native_ui")

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
            self.resources.offline = False
            service_resource_requests = self.resources.count("/rules.yaml")
            service_proxy_checks = self.resources.pending_proxy_checks
            service_cache = Path(os.environ["PROGRAMDATA"]) / "Mihomo Optimized" / "service" / "runtime" / "rules" / (self.resources.cache_name + ".yaml")
            require(not service_cache.exists() and self.wininet.read() == self.proxy_disabled,
                    "service_cold_start_prerequisites_not_met")
            service_start = time.monotonic()
            self.driver.invoke("desktop_start")
            tun = self.phase("running")
            require(tun.get("tun") is True and tun.get("service", {}).get("running") is True, "tun_service_not_running")
            tun_core_pid = self.managed_core(tun)["pid"]
            require(service_cache.is_file() and self.resources.count("/rules.yaml") > service_resource_requests,
                    "service_resource_home_not_prepared")
            self.resources.require_direct_preparation(service_proxy_checks)
            require(tun.get("systemProxy") is False and self.wininet.read() == self.proxy_disabled,
                    "service_cold_start_changed_system_proxy")
            self.report["resourceBootstrap"]["serviceStartupSeconds"] = round(time.monotonic() - service_start, 3)
            self.report["resourceBootstrap"]["coldServiceStartWithoutSystemProxy"] = True
            self.report["resourceBootstrap"]["servicePreparationProxyChecks"] = self.resources.pending_proxy_checks - service_proxy_checks
            self.mark("resource_bootstrap_service")
            self.mark("tun_service_identity")
            self.check_service_proxy_recovery(tun, mixed)

            # A recovery must run inside an already authenticated heartbeat.
            # Otherwise its >20s preparation would block the next pipe connect
            # (4s) and incorrectly expire the service's normal 20s UI lease.
            self.stage = "resource_bootstrap_recovery"
            old_controller = tun["controller"]
            old_identity = self.owned_children[tun_core_pid]
            require(old_identity.path == os.path.normcase(str(self.core.resolve()))
                    and self.processes.identity(tun_core_pid) == old_identity,
                    "recovery_target_identity_changed")
            service_cache.unlink()  # Only this fixture's random, verified cache.
            recovery_requests = self.resources.count("/rules.yaml")
            recovery_started = time.monotonic()
            self.processes.kill(old_identity)

            def recovered_status():
                observed = self.status()
                require("无法连接本用户的 TUN 服务" not in (observed.get("lastError") or ""),
                        "resource_recovery_blocked_service_connection")
                return observed if observed.get("phase") == "running" and observed.get("controller") != old_controller else None

            tun = wait_until(recovered_status, "service_resource_recovery_timeout", self.resources.delay + 25)
            tun_core_pid = self.managed_core(tun)["pid"]
            recovery_seconds = time.monotonic() - recovery_started
            require(tun_core_pid != old_identity.pid and recovery_seconds >= self.resources.delay * 0.9
                    and service_cache.is_file() and self.resources.count("/rules.yaml") > recovery_requests,
                    "service_recovery_did_not_prepare_resources")
            time.sleep(2.2)
            tun = self.status()
            require(tun.get("tun") is True and tun.get("service", {}).get("running") is True
                    and self.managed_core(tun)["pid"] == tun_core_pid,
                    "service_lease_stopped_recovered_core")
            wait_until(self.socks.roundtrip, "recovered_tun_forwarding_timeout")
            self.report["resourceBootstrap"].update({
                "serviceRecoverySeconds": round(recovery_seconds, 3),
                "serviceRecoveryNewPid": True, "serviceLeaseMaintained": True,
                "serviceRecoveryTunForwarded": True,
            })
            self.mark("resource_bootstrap_recovery")
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
        require(self.wininet.read() == self.proxy_disabled, "proxy_changed_during_tun_test")
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
        wait_until(lambda: self.wininet.read() == self.proxy_disabled, "watchdog_did_not_restore_system_proxy")
        wait_until(lambda: not self.processes.at_paths([self.host, self.core]) and all(not listening(port) for port in self.ports), "host_did_not_stop_after_gui_crash")
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
        for identity in self.processes.at_paths([self.app, self.host, self.core, self.helper]):
            attempt("owned_process_termination", lambda identity=identity: self.processes.kill(identity))
        for identity in self.owned_children.values():
            if self.processes.identity(identity.pid) == identity:
                attempt("owned_child_termination", lambda identity=identity: self.processes.kill(identity))
        if self.socks:
            attempt("socks_fixture_close", self.socks.close)
        if self.resources:
            attempt("resource_fixture_close", self.resources.close)
            for runtime in (self.directory / "runtime", Path(os.environ["PROGRAMDATA"]) / "Mihomo Optimized" / "service" / "runtime"):
                for suffix in (".yaml", "-timeout.yaml"):
                    path = runtime / "rules" / (self.resources.cache_name + suffix)
                    attempt("resource_fixture_cache_cleanup", lambda path=path: path.unlink(missing_ok=True))
        if self.update_cache:
            attempt("update_cache_fixture_cleanup", self.update_cache.close)
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
        attempt("process_cleanup_check", lambda: require(not self.processes.at_paths([self.app, self.host, self.core, self.helper]), "owned_process_remains"))
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
        expected = (Path(os.environ["ProgramFiles"]) / "Mihomo Optimized" / "mihomo-optimized.exe").resolve()
        require(app == expected and app.is_file(), "installed_application_missing")
        acceptance = Acceptance(app, artifacts)
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
            required = {"native_host_parent_binding", "update_cache_cleanup", "automatic_startup_ready", "startup_recovery", "config_revision", "system_proxy_restore", "autostart_restore",
                        "single_instance_and_native_ui", "tun_forwarding", "stop_cleanup", "crash_cleanup", "final_cleanup",
                        "non_elevated_gui", "tun_service_identity", "resource_bootstrap", "resource_bootstrap_service",
                        "resource_bootstrap_recovery", "system_proxy_service_recovery"}
            acceptance.report["passed"] = all(acceptance.report["tests"].get(name) is True for name in required) and not acceptance.report["diagnostics"]
            (artifacts / "verification.json").write_text(json.dumps(acceptance.report, indent=2) + "\n", encoding="utf-8")
    return 0 if acceptance and acceptance.report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
