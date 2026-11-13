#!/usr/bin/env python3
"""Run real ordinary/managed Windows TUN compatibility acceptance.

Run elevated in a disposable Windows x64 environment:
  python scripts/test-windows-tun-compat.py --core mihomo.exe \
      --fixture tun-compat.test.exe --report artifacts/report.json

The fixture is the sing-tun Go test binary, built with `go test -c` in
third_party/sing-tun. Its private owner holds a real Wintun adapter/session.
Ordinary cores use Clash Verge Rev's -d/-f/-ext-ctl-pipe command shape. A raw
socket, a /32 TUN route and a loopback SOCKS server verify actual forwarding.
No adapter is removed, renamed or repaired by this test. Only held child
processes/jobs and their own devices are closed. A failed launch is never
retried by this harness. GET readiness probes may repeat; mutations do not.

The report's sibling evidence directory is retained on success and failure.
Normal TUN close followed by forced process exit is reported as two separate
actions; it is not represented as graceful whole-process shutdown. The real
ordinary /restart endpoint is also exercised, including its replacement PID.
"""

from __future__ import annotations

import argparse
import ctypes
from datetime import datetime, timezone
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import queue
import re
import secrets
import socket
import socketserver
import struct
import subprocess
import sys
import threading
import time
import traceback
import uuid


SCRIPT = Path(__file__).resolve()
TARGET = "198.18.77.77"
START_TIMEOUT = 45.0
DWORD = ctypes.c_uint32
HANDLE = ctypes.c_void_p
WAIT_OBJECT_0, WAIT_TIMEOUT = 0, 258


class Failure(RuntimeError):
    pass


def require(condition: object, message: str) -> None:
    if not condition:
        raise Failure(message)


def timestamp() -> str:
    return datetime.now(timezone.utc).isoformat()


def file_hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def child_environment() -> dict[str, str]:
    allowed = {"SYSTEMROOT", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP",
               "SYSTEMDRIVE", "COMSPEC", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
               "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432"}
    # Inherited CLASH_*, MIHOMO_*, proxy overrides and CI credentials must not
    # turn an ordinary launch into managed mode or change its test traffic.
    return {key: value for key, value in os.environ.items() if key.upper() in allowed}


def expected_guid(name: str, namespace: str = "generic") -> str:
    prefix = "wintun" if namespace == "generic" else "mihomo-optimized/wintun-exclusive/v1\0"
    digest = hashlib.md5((prefix + name).encode(), usedforsecurity=False).digest()
    return "{" + str(uuid.UUID(bytes_le=digest)).upper() + "}"


class GUID(ctypes.Structure):
    _fields_ = [("a", DWORD), ("b", ctypes.c_uint16), ("c", ctypes.c_uint16),
                ("d", ctypes.c_ubyte * 8)]

    def text(self) -> str:
        return "{" + str(uuid.UUID(bytes_le=bytes(self))).upper() + "}"


class InterfaceRow(ctypes.Structure):
    _fields_ = [("luid", ctypes.c_uint64), ("index", DWORD), ("guid", GUID),
                ("alias", ctypes.c_uint16 * 257), ("description", ctypes.c_uint16 * 257),
                ("physical_address_length", DWORD), ("physical_address", ctypes.c_ubyte * 32),
                ("permanent_address", ctypes.c_ubyte * 32), ("mtu", DWORD), ("if_type", DWORD),
                ("tunnel_type", DWORD), ("media_type", DWORD), ("physical_medium_type", DWORD),
                ("access_type", DWORD), ("direction_type", DWORD), ("flags", ctypes.c_ubyte),
                ("oper_status", DWORD), ("admin_status", DWORD), ("unused", ctypes.c_ubyte * 188)]


class InterfaceTable(ctypes.Structure):
    _fields_ = [("count", DWORD), ("rows", InterfaceRow * 1)]


class FileTime(ctypes.Structure):
    _fields_ = [("low", DWORD), ("high", DWORD)]

    def number(self) -> int:
        return self.low | self.high << 32


class JobBasicLimits(ctypes.Structure):
    _fields_ = [("process_time", ctypes.c_int64), ("job_time", ctypes.c_int64),
                ("flags", DWORD), ("minimum_working_set", ctypes.c_size_t),
                ("maximum_working_set", ctypes.c_size_t), ("active_process_limit", DWORD),
                ("affinity", ctypes.c_size_t), ("priority", DWORD), ("scheduling", DWORD)]


class JobLimits(ctypes.Structure):
    _fields_ = [("basic", JobBasicLimits), ("io_counters", ctypes.c_uint64 * 6),
                ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
                ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t)]


def wide(value) -> str:
    return bytes(value).decode("utf-16-le").split("\0", 1)[0]


class Windows:
    def __init__(self) -> None:
        require(sys.platform == "win32" and ctypes.sizeof(HANDLE) == 8,
                "this acceptance requires Windows x64")
        require(ctypes.sizeof(InterfaceRow) == 1352 and InterfaceTable.rows.offset == 8
                and InterfaceRow.guid.offset == 12 and InterfaceRow.oper_status.offset == 1156
                and ctypes.sizeof(JobLimits) == 144, "Windows native layout mismatch")
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        signatures = {
            "CreateJobObjectW": ([HANDLE, ctypes.c_wchar_p], HANDLE),
            "SetInformationJobObject": ([HANDLE, ctypes.c_int, HANDLE, DWORD], ctypes.c_int),
            "AssignProcessToJobObject": ([HANDLE, HANDLE], ctypes.c_int),
            "IsProcessInJob": ([HANDLE, HANDLE, ctypes.POINTER(ctypes.c_int)], ctypes.c_int),
            "TerminateJobObject": ([HANDLE, ctypes.c_uint], ctypes.c_int),
            "OpenProcess": ([DWORD, ctypes.c_int, DWORD], HANDLE),
            "CloseHandle": ([HANDLE], ctypes.c_int),
            "WaitForSingleObject": ([HANDLE, DWORD], DWORD),
            "GetExitCodeProcess": ([HANDLE, ctypes.POINTER(DWORD)], ctypes.c_int),
            "GetProcessTimes": ([HANDLE, *([ctypes.POINTER(FileTime)] * 4)], ctypes.c_int),
            "QueryFullProcessImageNameW": ([HANDLE, DWORD, ctypes.c_wchar_p, ctypes.POINTER(DWORD)], ctypes.c_int),
        }
        for name, (arguments, result) in signatures.items():
            function = getattr(self.api, name)
            function.argtypes, function.restype = arguments, result
        self.ip = ctypes.WinDLL("iphlpapi", use_last_error=True)
        self.ip.GetIfTable2Ex.argtypes = [ctypes.c_int, ctypes.POINTER(HANDLE)]
        self.ip.GetIfTable2Ex.restype = DWORD
        self.ip.FreeMibTable.argtypes, self.ip.FreeMibTable.restype = [HANDLE], None
        shell = ctypes.WinDLL("shell32", use_last_error=True)
        shell.IsUserAnAdmin.restype = ctypes.c_int
        require(shell.IsUserAnAdmin(), "real Wintun acceptance requires an elevated process")

    def check(self, result: object, operation: str) -> None:
        require(result, f"{operation} failed: winerror={ctypes.get_last_error()}")

    def job(self):
        handle = self.api.CreateJobObjectW(None, None)
        self.check(handle, "CreateJobObject")
        limits = JobLimits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        try:
            self.check(self.api.SetInformationJobObject(handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)),
                       "SetInformationJobObject")
            return handle
        except BaseException:
            self.api.CloseHandle(handle)
            raise

    def hold(self, pid: int, executable: Path, job=None, *, assign: bool = False) -> dict:
        handle = self.api.OpenProcess(0x100000 | 0x1000 | 0x100 | 1, False, pid)
        self.check(handle, "OpenProcess")
        try:
            image, capacity = ctypes.create_unicode_buffer(32768), DWORD(32768)
            self.check(self.api.QueryFullProcessImageNameW(handle, 0, image, ctypes.byref(capacity)),
                       "QueryFullProcessImageName")
            require(os.path.normcase(os.path.abspath(image.value)) == os.path.normcase(str(executable)),
                    "controller PID does not identify the test core executable")
            if assign:
                self.check(self.api.AssignProcessToJobObject(job, handle), "AssignProcessToJobObject")
            if job:
                belongs = ctypes.c_int()
                self.check(self.api.IsProcessInJob(handle, job, ctypes.byref(belongs)), "IsProcessInJob")
                require(belongs.value, "replacement core escaped the owned job")
            result = {"pid": pid, "path": image.value, "handle": handle}
            result.update(self.process_state(result))
            return result
        except BaseException:
            self.api.CloseHandle(handle)
            raise

    def process_state(self, held: dict) -> dict:
        state = self.api.WaitForSingleObject(held["handle"], 0)
        require(state in (WAIT_OBJECT_0, WAIT_TIMEOUT), "owned process wait failed")
        times = [FileTime() for _ in range(4)]
        self.check(self.api.GetProcessTimes(held["handle"], *(ctypes.byref(value) for value in times)),
                   "GetProcessTimes")
        code = DWORD()
        self.check(self.api.GetExitCodeProcess(held["handle"], ctypes.byref(code)), "GetExitCodeProcess")
        return {"pid": held["pid"], "createdFileTime": times[0].number(),
                "exitedFileTime": times[1].number(), "exited": state == WAIT_OBJECT_0,
                "exitCode": code.value if state == WAIT_OBJECT_0 else None}

    def interfaces(self, name: str) -> list[dict]:
        table = HANDLE()
        result = self.ip.GetIfTable2Ex(0, ctypes.byref(table))
        require(result == 0 and table.value, f"GetIfTable2Ex failed: {result}")
        try:
            count = ctypes.cast(table, ctypes.POINTER(DWORD)).contents.value
            require(count <= 16384, "interface table exceeds limit")
            rows = (InterfaceRow * count).from_address(table.value + InterfaceTable.rows.offset)
            guids = {expected_guid(name), expected_guid(name, "product")}
            return [{"alias": wide(row.alias), "description": wide(row.description),
                     "guid": row.guid.text(), "luid": row.luid, "index": row.index,
                     "if_type": row.if_type, "oper_status": row.oper_status,
                     "admin_status": row.admin_status}
                    for row in rows if wide(row.alias).casefold() == name.casefold() or row.guid.text() in guids]
        finally:
            self.ip.FreeMibTable(table)

    def generic(self, name: str, *, retiring_product: dict | None = None,
                alive=None, observation: dict | None = None) -> dict:
        """Inspect one live generic interface after the core has already started.

        Only a second row proven to be this case's exited product creator may
        retire asynchronously. This wait never precedes creation and performs
        no launch, driver call, repair, rename or device removal.
        """
        observation = observation if observation is not None else {}
        started, polls, initial_generic = time.monotonic(), 0, None
        while True:
            if alive is not None:
                require(alive(), "core exited during interface identity observation")
            rows = self.interfaces(name)
            polls += 1
            observation.setdefault("first", rows)
            observation.update({"final": rows, "polls": polls,
                                "elapsedMilliseconds": (time.monotonic() - started) * 1000,
                                "retiringProductObserved": polls > 1})
            if alive is not None:
                require(alive(), "core exited while reading the interface table")
            current = [row for row in rows if row["guid"] == expected_guid(name)]
            require(len(current) == 1, f"generic GUID does not identify one current interface: {rows}")
            row = current[0]
            require(row["alias"] == name and row["luid"] > 0 and row["index"] > 0 and row["if_type"] == 53,
                    f"ordinary core did not retain its generic identity and original name: {row}")
            identity = tuple(row[key] for key in ("guid", "luid", "index", "alias", "if_type"))
            if initial_generic is None:
                initial_generic = identity
            require(identity == initial_generic, "new generic interface changed while the old product row retired")
            if len(rows) == 1:
                observation["settled"] = True
                return row
            retired = [candidate for candidate in rows if candidate is not row]
            require(retiring_product is not None and alive is not None and len(retired) == 1
                    and retiring_product.get("guid", "").upper() == expected_guid(name, "product")
                    and retiring_product.get("alias") == name
                    and retired[0]["guid"] == expected_guid(name, "product")
                    and all(retired[0][key] == retiring_product.get(key) for key in ("luid", "index", "if_type")),
                    f"unexpected interface identity/alias conflict after generic creation: {rows}")
            # Upstream native name assignment may rename this exact retiring
            # device. Its former creator has exited; keep GUID/LUID/index/type
            # as proof and record the alias change instead of treating it as
            # a different device. The new generic alias is still exact above.
            observation.update({"retiringProductObserved": True, "settled": False,
                                "retiringProductInstance": retiring_product.get("pnp_instance_id"),
                                "retiringProductAlias": retired[0]["alias"],
                                "retiringProductAliasChanged": retired[0]["alias"] != retiring_product["alias"]})
            require(time.monotonic() - started < 10,
                    f"exited product interface row did not retire within 10 seconds: {rows}")
            time.sleep(min(0.05, max(0, 10 - (time.monotonic() - started))))


class PipeSocket:
    def __init__(self, stream) -> None:
        self.stream = stream

    def makefile(self, *_args, **_kwargs):
        return self.stream


def pipe_helper(pipe: str) -> int:
    """One potentially blocking Windows request, bounded by the parent process.

    A separate helper allows the parent to terminate a stuck ReadFile without
    abandoning a thread or repeating a mutating request. No TCP fallback exists.
    """
    require(sys.platform == "win32", "named pipes require Windows")
    require(pipe == r"\\.\pipe\clash-verge-service"
            or re.fullmatch(r"\\\\\.\\pipe\\(?:mihomo-compat-[a-z0-9-]+|verge-mihomo-production-[0-9a-f]{64})", pipe),
            "unexpected test pipe")
    import msvcrt

    command = json.loads(sys.stdin.buffer.read(1024 * 1024 + 1))
    method, path, body = command["method"], command["path"], command.get("body")
    require(method in {"GET", "POST", "PATCH", "PUT", "DELETE"}
            and path in {"/version", "/configs", "/configs?force=true", "/desktop", "/restart",
                         "/status", "/clash/start", "/clash/stop", "/clash/log-snapshot"},
            "unexpected test controller request")
    headers = command.get("headers") or {}
    require(isinstance(headers, dict) and all(isinstance(key, str) and re.fullmatch(r"[A-Za-z0-9-]+", key)
            and isinstance(value, str) and "\r" not in value and "\n" not in value
            for key, value in headers.items()), "invalid fixture request headers")
    payload = b"" if body is None else json.dumps(body).encode()
    header = (f"{method} {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n"
              f"Content-Type: application/json\r\nContent-Length: {len(payload)}\r\n"
              + "".join(f"{key}: {value}\r\n" for key, value in headers.items()) + "\r\n").encode()
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetNamedPipeServerProcessId.argtypes = [HANDLE, ctypes.POINTER(DWORD)]
    kernel.GetNamedPipeServerProcessId.restype = ctypes.c_int
    with open(pipe, "r+b", buffering=0) as stream:
        pid = DWORD()
        require(kernel.GetNamedPipeServerProcessId(msvcrt.get_osfhandle(stream.fileno()), ctypes.byref(pid)),
                f"cannot verify pipe server PID: {ctypes.get_last_error()}")
        require(command.get("expected_pid") is None or command["expected_pid"] == pid.value,
                "named pipe belongs to a different process; request was not sent")
        wire = header + payload
        offset = 0
        while offset < len(wire):
            count = stream.write(wire[offset:])
            require(count, "pipe request made no progress")
            offset += count
        response = http.client.HTTPResponse(PipeSocket(io.BufferedReader(stream)))
        response.begin()
        data = response.read(1024 * 1024 + 1)
        require(len(data) <= 1024 * 1024, "pipe response exceeds limit")
        try:
            decoded = json.loads(data) if data else None
        except (ValueError, UnicodeError):
            decoded = data[:4096].decode("utf-8", errors="replace")
        print(json.dumps({"pid": pid.value, "status": response.status, "bytes": len(data), "body": decoded}))
    return 0


def pipe_request(pipe: str, method: str, path: str, body=None, *, timeout: float = 8,
                 headers: dict[str, str] | None = None, expected_pid: int | None = None) -> dict:
    completed = subprocess.run([sys.executable, str(SCRIPT), "--pipe-request", pipe],
        input=json.dumps({"method": method, "path": path, "body": body,
                          "headers": headers, "expected_pid": expected_pid}),
        text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        env=child_environment(), timeout=timeout, creationflags=subprocess.CREATE_NO_WINDOW)
    if completed.returncode:
        raise OSError(f"named-pipe request failed ({completed.returncode}): {completed.stderr[-2048:]}")
    return json.loads(completed.stdout)


def receive_exact(stream, count: int) -> bytes:
    data = bytearray()
    while len(data) < count:
        part = stream.recv(count - len(data))
        if not part:
            raise OSError("SOCKS fixture peer closed")
        data.extend(part)
    return bytes(data)


class SocksHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        stream = self.request
        stream.settimeout(5)
        try:
            version, count = receive_exact(stream, 2)
            require(version == 5 and 0 in receive_exact(stream, count), "unexpected SOCKS greeting")
            stream.sendall(b"\x05\x00")
            version, command, reserved, kind = receive_exact(stream, 4)
            if kind == 1:
                host = socket.inet_ntoa(receive_exact(stream, 4))
            elif kind == 3:
                host = receive_exact(stream, receive_exact(stream, 1)[0]).decode("ascii")
            else:
                raise Failure("unexpected SOCKS address type")
            port = struct.unpack("!H", receive_exact(stream, 2))[0]
            require((version, command, reserved, host, port) == (5, 1, 0, TARGET, 80),
                    "SOCKS fixture received an unexpected target")
            stream.sendall(b"\x05\x00\x00\x01\x7f\x00\x00\x01\x00\x00")
            request = bytearray()
            while b"\r\n\r\n" not in request and len(request) <= 8192:
                request.extend(receive_exact(stream, 1))
            require(request.startswith(f"GET {self.server.request_path} HTTP/1.1\r\n".encode()),
                    "unexpected forwarded HTTP request")
            body = self.server.body
            stream.sendall(f"HTTP/1.1 200 OK\r\nContent-Length: {len(body)}\r\nConnection: close\r\n\r\n".encode() + body)
            with self.server.lock:
                self.server.forwarded += 1
        except (OSError, ValueError, Failure) as error:
            with self.server.lock:
                self.server.errors.append(str(error))


class SocksFixture(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = False

    def __init__(self) -> None:
        self.lock, self.forwarded, self.errors = threading.Lock(), 0, []
        nonce = secrets.token_hex(16)
        self.request_path = "/mihomo-tun-compat/" + nonce
        self.body = ("real-windows-tun-forwarded-" + nonce + "\n").encode()
        super().__init__(("127.0.0.1", 0), SocksHandler)
        self.thread = threading.Thread(target=self.serve_forever, daemon=True)
        self.thread.start()

    def roundtrip(self) -> None:
        # AF_INET sends directly to the test /32. No proxy API or public
        # destination can return the nonce body from this loopback SOCKS server.
        with socket.create_connection((TARGET, 80), timeout=2) as stream:
            stream.sendall((f"GET {self.request_path} HTTP/1.1\r\nHost: {TARGET}\r\n"
                            "Connection: close\r\n\r\n").encode())
            response = http.client.HTTPResponse(stream)
            response.begin()
            require(response.status == 200 and response.read(4096) == self.body,
                    "TUN data path returned the wrong response")

    def close(self) -> None:
        self.shutdown()
        self.server_close()
        self.thread.join(timeout=3)


def verify_tun_forwarding(fixture: SocksFixture, *, timeout: float = 15, alive=None) -> dict:
    """Verify the real /32 data path; readiness probes never restart a core."""
    before, deadline, last = fixture.forwarded, time.monotonic() + timeout, None
    attempts = 0
    while time.monotonic() < deadline:
        if alive is not None:
            require(alive(), "core exited during forwarding")
        attempts += 1
        try:
            fixture.roundtrip()
            if fixture.forwarded > before:
                require(not fixture.errors, f"SOCKS fixture errors: {fixture.errors}")
                return {"attempts": attempts, "before": before, "after": fixture.forwarded,
                        "viaRawIPv4Socket": True, "lastSocketError": last}
        except (OSError, http.client.HTTPException) as error:
            last = str(error)
        time.sleep(0.05)
    raise Failure(f"real TUN forwarding failed: {last}")


def configuration(name: str, socks_port: int) -> dict:
    return {"mixed-port": 0, "port": 0, "socks-port": 0, "allow-lan": False,
            "external-controller": "", "external-controller-pipe": "", "secret": "",
            "bind-address": "127.0.0.1", "mode": "rule", "log-level": "info",
            "ipv6": False, "find-process-mode": "off", "geo-auto-update": False,
            "core-auto-update": False, "external-ui-auto-update": False,
            "profile": {"store-selected": False, "store-fake-ip": False},
            "dns": {"enable": False, "fake-ip-range": "198.18.0.1/16"}, "ntp": {"enable": False},
            "tun": {"enable": True, "device": name, "stack": "system", "auto-route": True,
                    "auto-detect-interface": True, "route-address": [TARGET + "/32"],
                    "dns-hijack": [], "dns-mode": "disabled"},
            "proxies": [{"name": "compat-socks", "type": "socks5", "server": "127.0.0.1",
                         "port": socks_port, "udp": False}],
            "rules": [f"IP-CIDR,{TARGET}/32,compat-socks,no-resolve", "MATCH,DIRECT"]}


IDENTITY_FIELDS = ("name", "namespace", "requested_guid", "guid", "luid", "index", "alias",
                   "description", "pnp_instance_id", "pnp_name", "pnp_tunnel_type",
                   "pnp_friendly_name", "pnp_hardware_ids", "pnp_class_guid")


def same_owner(before: dict, after: dict) -> None:
    require(all(before.get(key) == after.get(key) for key in IDENTITY_FIELDS),
            "existing native owner's identity changed")
    require(after.get("pnp_status_known") is True and after.get("pnp_phantom") is False,
            "existing native owner stopped being a present device")


class OwnerFixture:
    def __init__(self, case: Case, namespace: str, session: bool) -> None:
        self.case, self.events, self.last = case, queue.Queue(), None
        self.closed = False
        self.log_path = case.directory / (namespace + "-owner.log")
        self.log = self.log_path.open("w", encoding="utf-8")
        environment = child_environment()
        environment.update({"MIHOMO_TUN_COMPAT_FIXTURE": "1", "MIHOMO_TUN_COMPAT_NAME": case.name,
                            "MIHOMO_TUN_COMPAT_NAMESPACE": namespace,
                            "MIHOMO_TUN_COMPAT_SESSION": "1" if session else "0"})
        self.process = subprocess.Popen([str(case.suite.fixture), "-test.run=^TestWindowsCompatOwnerFixture$",
                                         "-test.timeout=2m"], env=environment,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", bufsize=1, creationflags=subprocess.CREATE_NO_WINDOW)
        case.owners.append(self)
        self.record = {"pid": self.process.pid, "namespace": namespace, "initialSession": session,
                       "log": str(self.log_path), "events": []}
        case.record.setdefault("owners", []).append(self.record)
        self.reader = threading.Thread(target=self.read_output, daemon=True)
        self.reader.start()
        ready = self.receive("ready", timeout=30)
        require(ready.get("pid") == self.process.pid and ready.get("guid", "").upper() == expected_guid(case.name, namespace)
                and ready.get("alias") == case.name and ready.get("name") == case.name
                and ready.get("session_active") is session and ready.get("luid", 0) > 0
                and ready.get("index", 0) > 0 and ready.get("pnp_instance_id"),
                "fixture did not establish its requested native identity")
        self.initial = ready

    def read_output(self) -> None:
        try:
            for line in self.process.stdout:
                self.log.write(line)
                self.log.flush()
                try:
                    event = json.loads(line)
                    if isinstance(event, dict) and "event" in event:
                        self.events.put(event)
                except ValueError:
                    pass
        finally:
            self.events.put(None)

    def receive(self, expected: str, *, token: str | None = None, timeout: float = 10) -> dict:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                event = self.events.get(timeout=max(0.01, deadline - time.monotonic()))
            except queue.Empty as error:
                raise Failure(f"owner fixture did not respond to {expected}") from error
            require(event is not None, f"owner fixture exited before {expected}")
            self.last = event
            self.record["events"].append(event)
            self.case.suite.checkpoint()
            require(event.get("event") != "error", f"owner fixture error: {event}")
            require(event.get("event") == expected and (token is None or event.get("id") == token),
                    f"owner fixture response did not match {expected}/{token}: {event}")
            return event
        raise Failure(f"owner fixture response deadline expired: {expected}")

    def command(self, command: str) -> dict:
        require(self.process.poll() is None, "owner command requires the retained live fixture")
        token = secrets.token_hex(8)
        self.process.stdin.write(json.dumps({"command": command, "id": token}) + "\n")
        self.process.stdin.flush()
        return self.receive({"close": "closed", "session_start": "session_started"}.get(command, command), token=token)

    def close(self, *, crash: bool = False, allow_exited: bool = False) -> None:
        if self.closed:
            return
        require(allow_exited or self.process.poll() is None,
                "owner exited unexpectedly before the intended close/crash transition")
        if self.process.poll() is None:
            if crash:
                self.process.kill()
                self.record["stopKind"] = "TerminateProcess_without_EndSession_or_CloseAdapter"
            else:
                event = self.command("close")
                require(event.get("identity_before_close") is True and event.get("session_active") is False,
                        "fixture close acknowledgment has invalid semantics")
                self.record["stopKind"] = "EndSession_CloseAdapter_then_process_exit"
            code = self.process.wait(timeout=10)
            require(code != 0 if crash else code == 0,
                    "native owner exit did not match the requested crash/normal-close transition")
        else:
            self.record["stopKind"] = "already_exited_observed_during_cleanup"
        self.record["exitCode"] = self.process.returncode
        self.record["exitConfirmedAt"] = timestamp()
        self.closed = True

    def cleanup(self) -> None:
        try:
            self.close(allow_exited=True)
        finally:
            if self.process.poll() is None:
                self.process.kill()
                self.process.wait(timeout=10)
                self.record["cleanupForced"] = True
            if self.process.stdin:
                self.process.stdin.close()
            self.reader.join(timeout=3)
            if not self.reader.is_alive():
                self.log.close()


class Core:
    def __init__(self, case: Case, label: str, *, managed: bool = False) -> None:
        self.case, self.suite, self.managed = case, case.suite, managed
        self.home = case.directory / label
        self.home.mkdir()
        self.config_path = self.home / "config.yaml"
        self.config = configuration(case.name, self.suite.socks.server_address[1])
        self.config_path.write_text(json.dumps(self.config, indent=2), encoding="utf-8")
        self.pipe = r"\\.\pipe\mihomo-compat-" + secrets.token_hex(16)
        self.environment = child_environment()
        self.arguments = [str(self.suite.core), "-d", str(self.home), "-f", str(self.config_path)]
        self.secret = self.nonce = ""
        if managed:
            self.secret, self.nonce = secrets.token_urlsafe(32), secrets.token_hex(32)
            self.arguments.extend(["-desktop-managed", "-ext-ctl", "127.0.0.1:0"])
            self.environment.update({"CLASH_OVERRIDE_SECRET": self.secret, "MIHOMO_DESKTOP_LAUNCH_NONCE": self.nonce})
        else:
            self.arguments.extend(["-ext-ctl-pipe", self.pipe])
            # The elevated disposable test caller belongs to BA. CVR likewise
            # supplies an explicit named-pipe SDDL for its authorized owner.
            self.environment["LISTEN_NAMEDPIPE_SDDL"] = "D:P(A;;GA;;;SY)(A;;GA;;;BA)"
        self.log_path = self.home / "core.log"
        self.output = self.log_path.open("wb")
        self.job, self.process, self.current = None, None, None
        self.handles: list[dict] = []
        self.record = {"managed": managed, "arguments": self.arguments, "log": str(self.log_path),
                       "home": str(self.home), "launchCount": 0, "controllerResponses": []}
        case.record.setdefault("cores", []).append(self.record)
        case.cores.append(self)

    def start(self) -> None:
        require(self.process is None, "the test must never retry a failed core launch")
        self.job = self.suite.windows.job()
        self.started = time.monotonic()
        self.process = subprocess.Popen(self.arguments, cwd=self.home, env=self.environment,
            stdin=subprocess.DEVNULL, stdout=self.output, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW)
        self.record.update({"launchCount": 1, "initialPid": self.process.pid, "startedAt": timestamp()})
        try:
            self.current = self.suite.windows.hold(self.process.pid, self.suite.core, self.job, assign=True)
            self.handles.append(self.current)
        except Failure:
            # A deliberate startup rejection may exit before assignment. The
            # exact Popen handle must confirm exit; expect_failure still checks
            # the original native cause. A live unassigned child is a failure.
            if self.process.poll() is None:
                raise
            self.record["exitedBeforeJobAssignment"] = self.process.returncode

    def request(self, method: str, path: str, body=None) -> dict:
        require(self.current is not None, "controller request requires an identified owned process")
        result = pipe_request(self.pipe, method, path, body, expected_pid=self.current["pid"])
        require(result["pid"] == self.current["pid"],
                "controller response came from a different process")
        observed = {"method": method, "path": path,
                    "pid": result["pid"], "status": result["status"], "bytes": result["bytes"]}
        if path == "/configs" and isinstance(result["body"], dict):
            configuration = result["body"]
            tun = configuration.get("tun")
            observed["configuration"] = {"mode": configuration.get("mode"),
                "log-level": configuration.get("log-level"),
                "tun": {key: tun.get(key) for key in ("enable", "device", "stack", "auto-route",
                    "auto-detect-interface", "route-address", "inet4-address", "dns-mode")}
                    if isinstance(tun, dict) else tun}
        self.record["controllerResponses"].append(observed)
        return result

    def ready(self, *, previous_pid: int | None = None) -> dict:
        require(not self.managed, "ordinary readiness must use the actual named pipe")
        deadline, last = time.monotonic() + START_TIMEOUT, None
        while time.monotonic() < deadline:
            if previous_pid is None:
                require(self.process.poll() is None, "ordinary core exited before its named pipe became ready")
            try:
                result = pipe_request(self.pipe, "GET", "/version", timeout=2)
                require(result["status"] == 200 and isinstance(result["body"], dict)
                        and isinstance(result["body"].get("version"), str), "invalid /version response")
                if previous_pid is not None and result["pid"] == previous_pid:
                    time.sleep(0.05)
                    continue
                if result["pid"] != self.current["pid"]:
                    require(previous_pid is not None, "initial named pipe belongs to a different PID")
                    self.current = self.suite.windows.hold(result["pid"], self.suite.core, self.job)
                    self.handles.append(self.current)
                self.record.setdefault("versions", []).append(result)
                return self.verify()
            except (OSError, subprocess.TimeoutExpired) as error:
                last = str(error)
                time.sleep(0.05)
        raise Failure(f"ordinary core named-pipe readiness timed out: {last}")

    def verify(self) -> dict:
        require(not self.suite.windows.process_state(self.current)["exited"], "owned ordinary core is not alive")
        response = self.request("GET", "/configs")
        require(response["status"] == 200 and isinstance(response["body"], dict), "cannot read ordinary /configs")
        tun = response["body"].get("tun", {})
        require(tun.get("enable") is True and tun.get("device") == self.case.name
                and tun.get("stack") == "System" and tun.get("auto-route") is True,
                "ordinary runtime did not apply its original TUN configuration: "
                + json.dumps({"expectedDevice": self.case.name, "actualTun": tun}, sort_keys=True))
        require(self.request("GET", "/desktop")["status"] == 404
                and not (self.home / "desktop-controller.json").exists(),
                "ordinary startup/reload/restart unexpectedly entered managed mode")
        observation = {}
        self.record.setdefault("identityObservations", []).append(observation)
        try:
            identity = self.suite.windows.generic(self.case.name,
                retiring_product=self.case.retiring_product,
                alive=lambda: not self.suite.windows.process_state(self.current)["exited"],
                observation=observation)
        finally:
            self.suite.checkpoint()
        self.record.setdefault("identities", []).append(identity)
        self.suite.checkpoint()
        return identity

    def forwarding(self, label: str) -> None:
        result = verify_tun_forwarding(self.suite.socks,
            alive=lambda: not self.suite.windows.process_state(self.current)["exited"])
        self.record.setdefault("forwarding", []).append({"stage": label, **result})
        self.suite.checkpoint()

    def reload(self) -> None:
        before, old_pid = self.verify(), self.current["pid"]
        self.config["log-level"] = "warning"
        self.config_path.write_text(json.dumps(self.config, indent=2), encoding="utf-8")
        result = self.request("PUT", "/configs?force=true", {"path": str(self.config_path)})
        require(result["status"] == 204, f"ordinary hot reload failed: {result}")
        after = self.verify()
        require(self.current["pid"] == old_pid
                and all(after[key] == before[key] for key in ("guid", "luid", "index", "alias", "if_type")),
                "an unchanged ordinary TUN was replaced or changed identity during hot reload")
        actual_level = self.request("GET", "/configs")["body"].get("log-level")
        require(actual_level == "warning", f"hot reload did not apply log-level warning: actual={actual_level!r}")
        self.record["hotReload"] = {"pid": old_pid, "sameIdentity": True, "modeRemainedOrdinary": True}
        self.forwarding("after_hot_reload")

    def restart(self) -> None:
        old = self.current
        before = self.verify()
        result = self.request("POST", "/restart")
        require(result["status"] == 200 and result["body"] == {"status": "ok"},
                f"ordinary restart was rejected or returned an invalid acknowledgment: {result}")
        after = self.ready(previous_pid=old["pid"])
        require(self.suite.windows.api.WaitForSingleObject(old["handle"], 10000) == WAIT_OBJECT_0,
                "old core process survived an acknowledged ordinary restart")
        old_state = self.suite.windows.process_state(old)
        require(old_state["exitCode"] == 0 and self.current["pid"] != old["pid"],
                "ordinary restart did not complete a successful process replacement")
        require(after["guid"] == before["guid"] and after["alias"] == before["alias"],
                "ordinary process restart changed the TUN namespace or alias")
        self.record.setdefault("restarts", []).append({"previous": old_state,
            "replacement": self.suite.windows.process_state(self.current), "modeRemainedOrdinary": True})
        self.forwarding("after_ordinary_restart")

    def expect_failure(self, marker: str) -> None:
        code = self.process.wait(timeout=25)
        self.output.flush()
        log = self.log_path.read_text(encoding="utf-8", errors="replace")
        require(code != 0 and marker in log, f"core did not fail with expected native cause {marker!r}; see {self.log_path}")
        self.record["expectedFailure"] = {"marker": marker, "exitCode": code, "exitConfirmed": True}
        if not self.managed:
            require("reason=foreign_alias" not in log, "ordinary path still used the managed ownership check")
        self.suite.checkpoint()

    def stop(self) -> None:
        if self.job is None:
            return
        if self.current and not self.suite.windows.process_state(self.current)["exited"] and not self.managed:
            result = self.request("PATCH", "/configs", {"tun": {"enable": False}})
            require(result["status"] == 204, "ordinary TUN shutdown request failed")
            observed = self.request("GET", "/configs")
            require(observed["status"] == 200 and observed["body"].get("tun", {}).get("enable") is False,
                    "ordinary TUN disable was not confirmed")
            self.record["tunShutdown"] = "PATCH_tun_false_confirmed_before_forced_process_exit"
        self.kill_owned_job()

    def kill_owned_job(self) -> None:
        if self.job is None:
            return
        self.suite.windows.check(self.suite.windows.api.TerminateJobObject(self.job, 87), "TerminateJobObject")
        for held in self.handles:
            require(self.suite.windows.api.WaitForSingleObject(held["handle"], 10000) == WAIT_OBJECT_0,
                    "owned core did not exit during cleanup")
        if self.process:
            self.process.wait(timeout=10)
        self.record["processStop"] = "owned_job_TerminateProcess_after_optional_TUN_disable"
        self.record["processes"] = [self.suite.windows.process_state(held) for held in self.handles]
        self.suite.windows.api.CloseHandle(self.job)
        self.job = None

    def cleanup(self) -> None:
        try:
            self.stop()
        finally:
            try:
                try:
                    self.kill_owned_job()
                finally:
                    try:
                        if self.job is not None:
                            # KILL_ON_JOB_CLOSE also covers a failed job wait;
                            # descendants must not survive a cleanup exception.
                            self.suite.windows.api.CloseHandle(self.job)
                            self.job = None
                    finally:
                        if self.process and self.process.poll() is None:
                            # An unassigned Popen child is not in the job. This
                            # fallback must run even when job termination/wait
                            # raised, and acts only on our exact process handle.
                            self.process.kill()
                            self.process.wait(timeout=10)
            finally:
                self.output.close()
                for held in self.handles:
                    self.suite.windows.api.CloseHandle(held["handle"])
                self.handles.clear()
                if self.secret:
                    log = self.log_path.read_text(encoding="utf-8", errors="replace")
                    self.log_path.write_text(log.replace(self.secret, "<secret>").replace(self.nonce, "<nonce>"),
                                             encoding="utf-8")


class Case:
    def __init__(self, suite: Suite, label: str) -> None:
        self.suite, self.label = suite, label
        self.name = "MihomoCompat-" + secrets.token_hex(8)
        self.directory = suite.evidence / label
        self.directory.mkdir()
        self.record = {"startedAt": timestamp(), "name": self.name, "status": "running",
                       "genericGuid": expected_guid(self.name), "productGuid": expected_guid(self.name, "product")}
        suite.report["scenarios"][label] = self.record
        self.cores: list[Core] = []
        self.owners: list[OwnerFixture] = []
        self.retiring_product: dict | None = None
        require(not suite.windows.interfaces(self.name), "random test alias/GUID already exists")
        suite.checkpoint()

    def snapshot(self, label: str) -> None:
        snapshot = {"capturedAt": timestamp(), "stage": label, "cores": [], "owners": []}
        try:
            snapshot["interfaces"] = self.suite.windows.interfaces(self.name)
        except Exception as error:
            snapshot["interfaceError"] = repr(error)
        for core in self.cores:
            states = []
            for held in core.handles:
                try:
                    states.append(self.suite.windows.process_state(held))
                except Exception as error:
                    states.append({"pid": held["pid"], "error": repr(error)})
            snapshot["cores"].append({"log": str(core.log_path), "processes": states})
        for owner in self.owners:
            observed = {"pid": owner.process.pid, "exitCode": owner.process.poll(), "lastEvent": owner.last}
            if owner.process.poll() is None:
                try:
                    observed["inspection"] = owner.command("inspect")
                except Exception as error:
                    observed["inspectionError"] = repr(error)
            snapshot["owners"].append(observed)
        path = self.directory / (label + ".json")
        path.write_text(json.dumps(snapshot, indent=2), encoding="utf-8")
        self.record.setdefault("snapshots", []).append(str(path))
        self.suite.checkpoint()

    def cleanup(self) -> None:
        errors = []
        # Borrowers must finish before their native creator fixture closes.
        for child in [*reversed(self.cores), *reversed(self.owners)]:
            try:
                child.cleanup()
            except Exception as error:
                errors.append(repr(error))
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            rows = self.suite.windows.interfaces(self.name)
            if not rows:
                break
            time.sleep(0.1)
        self.record["cleanup"] = {"errors": errors, "remainingInterfaces": rows,
                                  "externalDeviceRemoval": False}
        self.suite.checkpoint()
        require(not errors and not rows, f"owned fixture cleanup incomplete: {self.record['cleanup']}")


class Suite:
    def __init__(self, core: Path, fixture: Path, report_path: Path) -> None:
        self.core, self.fixture, self.report_path = core, fixture, report_path
        self.report_path.parent.mkdir(parents=True, exist_ok=True)
        self.evidence = report_path.with_name(report_path.stem + "-evidence-" + secrets.token_hex(4))
        self.evidence.mkdir()
        self.report = {"schemaVersion": 1, "startedAt": timestamp(), "status": "running",
            "core": {"path": str(core), "sha256": file_hash(core)},
            "fixture": {"path": str(fixture), "sha256": file_hash(fixture)},
            "evidenceDirectory": str(self.evidence), "scenarios": {},
            "scope": {"realNativeWintun": True, "realCore": True, "realNamedPipe": True,
                      "realTunTraffic": True, "realClashVergeServiceInstalled": False}}
        self.checkpoint()
        self.windows = Windows()
        self.socks = SocksFixture()

    def checkpoint(self) -> None:
        self.report_path.write_text(json.dumps(self.report, indent=2), encoding="utf-8")

    def run_case(self, label: str, action) -> None:
        case = Case(self, label)
        failure = None
        try:
            action(case)
            case.snapshot("completed")
            case.record["status"] = "passed"
        except BaseException as error:
            failure = error
            case.record.update({"status": "failed", "error": repr(error)})
            (case.directory / "failure.txt").write_text(traceback.format_exc(), encoding="utf-8")
            case.snapshot("failure-before-cleanup")
        finally:
            try:
                case.cleanup()
            except BaseException as error:
                case.record["status"] = "failed"
                case.record["cleanupError"] = repr(error)
                failure = failure or error
            case.record["finishedAt"] = timestamp()
            self.checkpoint()
        if failure:
            raise failure

    def fresh_restart_reload(self, case: Case) -> None:
        core = Core(case, "ordinary")
        core.start()
        core.ready()
        core.forwarding("fresh_start")
        core.reload()
        for _ in range(3):
            core.restart()

    def borrow_generic(self, case: Case) -> None:
        owner = OwnerFixture(case, "generic", False)
        managed = Core(case, "managed-rejected", managed=True)
        managed.start()
        managed.expect_failure("reason=foreign_alias")
        after = owner.command("inspect")
        same_owner(owner.initial, after)
        require(after["session_active"] is False, "managed failure changed the old owner's session")
        core = Core(case, "ordinary-borrower")
        core.start()
        identity = core.ready()
        require(identity["luid"] == owner.initial["luid"] and identity["index"] == owner.initial["index"],
                "ordinary core did not use the existing generic adapter")
        same_owner(owner.initial, owner.command("inspect"))
        core.forwarding("borrowed_generic_adapter")
        core.stop()
        same_owner(owner.initial, owner.command("inspect"))
        started = owner.command("session_start")
        same_owner(owner.initial, started)
        probe = owner.command("session_probe")
        require(probe.get("session_active") is True and probe.get("probe", {}).get("read_event", 0) > 0,
                "borrower closed or damaged its original creator's adapter")
        case.record["creatorRetainedAfterBorrowerExit"] = True

    def active_generic(self, case: Case) -> None:
        owner = OwnerFixture(case, "generic", True)
        before = owner.command("session_probe")
        core = Core(case, "ordinary-contender")
        core.start()
        core.expect_failure("start Wintun session")
        after = owner.command("session_probe")
        same_owner(before, after)
        require(after["session_active"] is True
                and after["probe"]["read_event"] == before["probe"]["read_event"]
                and after["probe"]["receive"] in {"empty", "packet"},
                "contending ordinary core stole or damaged the existing active session")
        case.record["existingSessionSurvived"] = True

    def released_owner(self, case: Case, *, namespace: str, crash: bool) -> None:
        owner = OwnerFixture(case, namespace, True)
        core = Core(case, "ordinary-after-owner")  # Prepare all files before the handoff.
        owner.close(crash=crash)
        if namespace == "product":
            # close() has confirmed the exact old process exited. This proof
            # permits read-only observation of its row only after new startup.
            case.retiring_product = owner.initial
        exited = time.monotonic()
        # No disappearance polling, device removal, fixed grace sleep or
        # repeated launch is permitted between the old exit and this start.
        core.start()
        case.record["exitToSpawnMilliseconds"] = (core.started - exited) * 1000
        core.ready()
        core.forwarding("immediate_" + ("crash" if crash else "close") + "_replacement")

    def run(self) -> None:
        try:
            version = subprocess.run([str(self.core), "-v"], env=child_environment(),
                text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
            require(version.returncode == 0, "core version probe failed")
            self.report["core"]["versionOutput"] = version.stdout
            self.run_case("fresh_ordinary_restart_and_reload", self.fresh_restart_reload)
            self.run_case("idle_generic_managed_refuses_ordinary_borrows", self.borrow_generic)
            self.run_case("active_generic_session_is_not_stolen", self.active_generic)
            self.run_case("generic_normal_close_immediate_start",
                          lambda case: self.released_owner(case, namespace="generic", crash=False))
            self.run_case("generic_crash_immediate_start",
                          lambda case: self.released_owner(case, namespace="generic", crash=True))
            self.run_case("product_normal_close_returns_to_generic",
                          lambda case: self.released_owner(case, namespace="product", crash=False))
            self.run_case("product_crash_returns_to_generic",
                          lambda case: self.released_owner(case, namespace="product", crash=True))
            self.report["status"] = "passed"
        except BaseException as error:
            self.report.update({"status": "failed", "error": repr(error)})
            raise
        finally:
            self.socks.close()
            self.report["finishedAt"] = timestamp()
            self.checkpoint()


def main() -> int:
    if len(sys.argv) == 3 and sys.argv[1] == "--pipe-request":
        return pipe_helper(sys.argv[2])
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core", required=True, type=Path)
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    report_path = args.report.resolve()
    try:
        suite = Suite(args.core.resolve(strict=True), args.fixture.resolve(strict=True), report_path)
        suite.run()
    except BaseException as error:
        if report_path.exists():
            report = json.loads(report_path.read_text(encoding="utf-8"))
            report.update({"status": "failed", "error": repr(error), "finishedAt": timestamp()})
            report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
        else:
            report_path.parent.mkdir(parents=True, exist_ok=True)
            report_path.write_text(json.dumps({"status": "failed", "error": repr(error)}, indent=2), encoding="utf-8")
        print(f"Windows TUN compatibility failed: {error}\nReport: {report_path}", file=sys.stderr)
        return 1
    print(f"Windows TUN compatibility passed. Report: {report_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
