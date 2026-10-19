#!/usr/bin/env python3
"""Acceptance test for the installed Windows application on a disposable runner.

Uses Python's standard library, actual WebView2/EdgeDriver, actual WinInet, and a
local SOCKS fixture. No Tauri bridge or operating-system behavior is mocked.
See https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/webdriver
and https://learn.microsoft.com/en-us/microsoft-edge/webdriver/capabilities-edge-options

setup-windows.ps1 supplies MIHOMO_TEST_APP and MIHOMO_TEST_DRIVER. This test changes
the runner's own proxy/autostart settings and installs the app's TUN service; it
refuses to run outside a disposable GitHub Actions Windows runner.
"""

from __future__ import annotations

import base64
import contextlib
import ctypes
from ctypes import wintypes
from dataclasses import dataclass
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
import tempfile
import threading
import time
from urllib.parse import quote, urlsplit


SERVICE_NAME = "MihomoOptimizedService"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE = "MihomoOptimized"
TUN_TARGET = "198.18.77.77"
TUN_BODY = b"mihomo-windows-tun-socks-forwarded\n"
BAD_CONFIG = "mixed-port: [unterminated\n"


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

    def descendants(self, parents: set[int]) -> list[ProcessIdentity]:
        entries = self.entries()
        selected = set(parents)
        while True:
            found = {pid for pid, parent, _name in entries if parent in selected} - selected
            if not found:
                break
            selected.update(found)
        return [identity for pid in selected - parents if (identity := self.identity(pid))]

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


class WebViewDriver:
    def __init__(self, driver: Path, app: Path) -> None:
        self.port = free_port()
        self.session = None
        self.output = tempfile.TemporaryFile()
        # EdgeDriver inherits Chromium's mutually exclusive log-level/silent flags.
        self.process = subprocess.Popen([str(driver), f"--port={self.port}", "--log-level=SEVERE"],
                                        stdin=subprocess.DEVNULL, stdout=self.output, stderr=subprocess.STDOUT,
                                        env=child_environment(), creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            wait_until(self.ready, "edgedriver_start_timeout", 20)
            result = self.request("POST", "/session", {"capabilities": {"alwaysMatch": {
                "browserName": "webview2", "ms:edgeOptions": {"binary": str(app)},
            }}}, timeout=90)
            require(isinstance(result, dict) and isinstance(result.get("sessionId"), str), "webview_session_missing")
            self.session = result["sessionId"]
            self.request("POST", self.path("timeouts"), {"script": 90000, "pageLoad": 30000, "implicit": 0})
        except BaseException:
            self.close()
            raise

    def ready(self) -> bool:
        require(self.process.poll() is None, "edgedriver_exited")
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
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        self.output.close()


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
        try:
            version, count = receive_exact(stream, 2)
            methods = receive_exact(stream, count)
            if version != 5 or 0 not in methods:
                return
            stream.sendall(b"\x05\x00")
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
        except (OSError, ValueError, UnicodeError):
            return


class SocksFixture(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = False

    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.targets: list[tuple[str, int]] = []
        self.forwarded = 0
        super().__init__(("127.0.0.1", 0), SocksHandler)
        self.thread = threading.Thread(target=self.serve_forever, daemon=True)
        self.thread.start()

    def roundtrip(self) -> bool:
        # A raw AF_INET socket cannot use HTTP_PROXY, WinInet, or a SOCKS option.
        # Only the /32 route into the real TUN interface can reach this fixture.
        try:
            with socket.create_connection((TUN_TARGET, 80), timeout=2) as stream:
                stream.sendall(f"GET /mihomo-tun-smoke HTTP/1.1\r\nHost: {TUN_TARGET}\r\nConnection: close\r\n\r\n".encode())
                response = http.client.HTTPResponse(stream)
                response.begin()
                body = response.read(4096)
                require(response.status == 200 and body == TUN_BODY, "tun_unexpected_response")
            with self.lock:
                return self.forwarded >= 1 and (TUN_TARGET, 80) in self.targets
        except (OSError, http.client.HTTPException):
            return False

    def close(self) -> None:
        self.shutdown()
        self.server_close()
        self.thread.join(timeout=3)


def profile(mixed: int, socks: int) -> str:
    return f"""mixed-port: {mixed}
port: 0
socks-port: 0
allow-lan: false
bind-address: 127.0.0.1
mode: rule
log-level: warning
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
        self.directory = Path(os.environ["LOCALAPPDATA"]) / "Mihomo Optimized"
        self.processes = Processes()
        self.wininet = WinInet()
        self.proxy_before = self.wininet.read()
        self.autostart_before = read_autostart()
        self.original_files: dict[Path, bytes | None] = {}
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
        parents = set()
        if self.gui and self.processes.identity(self.gui.pid) == self.gui:
            parents.add(self.gui.pid)
        if self.driver and self.driver.process.poll() is None:
            parents.add(self.driver.process.pid)
        for identity in self.processes.descendants(parents):
            self.owned_children[identity.pid] = identity

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
        self.socks = SocksFixture()
        mixed = free_port()
        self.ports.add(mixed)
        valid = profile(mixed, self.socks.server_address[1])

        self.stage = "startup_recovery"
        self.driver = WebViewDriver(self.driver_path, self.app)
        gui = wait_until(lambda: self.processes.at_paths([self.app]), "gui_process_not_found")
        require(len(gui) == 1, "multiple_gui_processes")
        self.gui = gui[0]
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
        duplicate = subprocess.Popen([str(self.app)], cwd=self.app.parent, env=child_environment(),
                                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            require(duplicate.wait(timeout=12) == 0, "second_instance_exit_failed")
        except subprocess.TimeoutExpired:
            instance = self.processes.identity(duplicate.pid)
            if instance:
                self.processes.kill(instance)
                duplicate.wait(timeout=5)
            raise SmokeFailure("second_instance_did_not_exit") from None
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
        installed = self.driver.invoke("desktop_install_service")
        require(installed.get("service", {}).get("installed") and service_installed(), "tun_service_not_installed")
        self.driver.invoke("desktop_set_tun", {"enabled": True})
        self.driver.invoke("desktop_start")
        tun = self.phase("running")
        require(tun.get("tun") is True and tun.get("service", {}).get("running") is True, "tun_service_not_running")
        self.managed_core(tun)
        wait_until(self.socks.roundtrip, "tun_forwarding_timeout")
        with self.socks.lock:
            require(all(target == (TUN_TARGET, 80) for target in self.socks.targets), "tun_fixture_received_unexpected_target")
            self.report["tun"] = {"route": TUN_TARGET + "/32", "socksConnections": len(self.socks.targets), "forwardedRequests": self.socks.forwarded}
        self.mark("tun_forwarding")

        self.stage = "stop_cleanup"
        self.driver.invoke("desktop_stop")
        self.driver.invoke("desktop_set_tun", {"enabled": False})
        self.driver.invoke("desktop_uninstall_service")
        wait_until(lambda: not service_installed(), "tun_service_not_removed")
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
                if service_installed():
                    result = subprocess.run([str(self.helper), "--uninstall"], stdin=subprocess.DEVNULL,
                                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                            timeout=65, creationflags=subprocess.CREATE_NO_WINDOW)
                    require(result.returncode == 0, "cleanup_service_uninstall_failed")
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
                        "single_instance_and_webview2_ui", "tun_forwarding", "stop_cleanup", "crash_cleanup", "final_cleanup"}
            acceptance.report["passed"] = all(acceptance.report["tests"].get(name) is True for name in required) and not acceptance.report["diagnostics"]
            (artifacts / "verification.json").write_text(json.dumps(acceptance.report, indent=2) + "\n", encoding="utf-8")
    return 0 if acceptance and acceptance.report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
