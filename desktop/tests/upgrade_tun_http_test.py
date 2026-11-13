"""Controller response contracts for the Windows upgrade fixture, runnable on any OS."""

from __future__ import annotations

import ctypes
import http.server
import json
from pathlib import Path
import socket
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from upgrade_tun_native import DWORD, NativeTunIdentity, TcpOwnerRow, decode_loopback_listeners
from upgrade_tun_smoke import TunUpgradeAcceptance, read_controller_record
from windows_smoke import SmokeFailure


class ControllerHandler(http.server.BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        self.reply()

    def do_POST(self) -> None:
        self.reply()

    def reply(self) -> None:
        self.server.requests.append((self.command, self.path, self.headers.get("Authorization")))
        status, body = self.server.response
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            # The bounded reader may close before an oversized body finishes.
            pass

    def log_message(self, _format: str, *args: object) -> None:
        pass


class ControllerContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), ControllerHandler)
        self.server.daemon_threads = True
        self.server.requests = []
        self.server.response = (200, b"{}")
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": .02})
        self.thread.start()
        self.secret = "fixture-controller-secret-with-32-or-more-characters"
        self.controller = {"url": "http://127.0.0.1:" + str(self.server.server_port), "secret": self.secret}
        # The HTTP boundary has no native device dependency. Exercise the real
        # client against a loopback server without constructing Windows state.
        self.fixture = object.__new__(TunUpgradeAcceptance)
        self.fixture.evidence = {}
        self.checkpoints = 0
        self.fixture.checkpoint = self.checkpoint

    def checkpoint(self) -> None:
        self.checkpoints += 1

    def tearDown(self) -> None:
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.assertFalse(self.thread.is_alive())

    def test_read_endpoints_require_200_json_objects(self) -> None:
        for path in ("/desktop", "/desktop/health", "/configs", "/version"):
            with self.subTest(path=path):
                self.server.response = (200, b'{"fixture":true}')
                self.assertEqual(self.fixture.request(self.controller, path), {"fixture": True})
                observation = self.fixture.evidence["controllerResponses"][-1]
                self.assertEqual(observation["bodyKind"], "json_object")
                self.assertEqual(observation["expectedStatus"], 200)
                self.assertTrue(observation["accepted"])
                self.assertEqual(self.server.requests[-1], ("GET", path, "Bearer " + self.secret))

    def test_shutdown_requires_202_and_stopping_acknowledgement(self) -> None:
        self.server.response = (202, b'{"status":"stopping"}\n')
        self.assertEqual(self.fixture.request(self.controller, "/desktop/shutdown", "POST"), {"status": "stopping"})
        self.assertEqual(self.fixture.evidence["controllerResponses"][-1], {
            "method": "POST", "path": "/desktop/shutdown", "expectedStatus": 202, "status": 202,
            "bytes": len(self.server.response[1]), "bodyKind": "json_object", "accepted": True})
        self.assertEqual(self.checkpoints, 0, "A successful acknowledgement added disk I/O before the native handoff")

    def test_unexpected_success_codes_and_http_errors_remain_failures(self) -> None:
        for method, path, codes in (("POST", "/desktop/shutdown", (200, 201, 204, 401, 500)),
                                    ("GET", "/desktop", (201, 202, 204, 403, 500)),
                                    ("GET", "/desktop/health", (201, 202, 204, 403, 500)),
                                    ("GET", "/configs", (201, 202, 204, 403, 500)),
                                    ("GET", "/version", (201, 202, 204, 403, 500))):
            for status in codes:
                with self.subTest(method=method, path=path, status=status):
                    self.server.response = (status, b'{"status":"stopping"}')
                    with self.assertRaisesRegex(SmokeFailure, "^upgrade_controller_response_status$"):
                        self.fixture.request(self.controller, path, method)
                    observation = self.fixture.evidence["controllerResponses"][-1]
                    self.assertEqual(observation["status"], status)
                    self.assertFalse(observation["accepted"])

    def test_shutdown_cannot_acknowledge_empty_or_wrong_state(self) -> None:
        for body in (b"", b"[]", b"not json", b"{}", b'{"status":"running"}',
                     b'{"status":"stopping","extra":true}'):
            with self.subTest(body=body):
                self.server.response = (202, body)
                with self.assertRaises(SmokeFailure):
                    self.fixture.request(self.controller, "/desktop/shutdown", "POST")
                self.assertFalse(self.fixture.evidence["controllerResponses"][-1]["accepted"])

    def test_diagnostics_are_bounded_and_do_not_include_response_data(self) -> None:
        for body, kind in ((self.secret.encode(), "contains_secret"),
                           (b"x" * (1024 * 1024 + 32), "oversized"),
                           (b"untrusted body text", "invalid_json"), (b"[]", "json_non_object")):
            with self.subTest(kind=kind):
                self.server.response = (200, body)
                with self.assertRaisesRegex(SmokeFailure, "^upgrade_controller_response_body$"):
                    self.fixture.request(self.controller, "/desktop")
                observation = self.fixture.evidence["controllerResponses"][-1]
                self.assertEqual(observation["bodyKind"], kind)
                self.assertLessEqual(observation["bytes"], 1024 * 1024 + 1)
        evidence = json.dumps(self.fixture.evidence)
        self.assertNotIn(self.secret, evidence)
        self.assertNotIn("untrusted body text", evidence)
        self.assertNotIn(self.controller["url"], evidence)
        self.assertEqual(self.checkpoints, 4)


class ControllerDiscoveryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.home = Path(self.temporary.name)
        self.path = self.home / "desktop-controller.json"
        self.nonce = "ab" * 32
        self.record = {"protocolVersion": 1, "pid": 123, "controller": "127.0.0.1:65000",
                       "coreInstance": "fixture-core-instance", "launchNonce": self.nonce}

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def read(self):
        return read_controller_record(self.home, 123, self.nonce)

    def write(self, value) -> None:
        self.path.write_text(json.dumps(value), encoding="utf-8")

    def test_absence_is_pending_and_exact_record_is_discovered(self) -> None:
        self.assertIsNone(self.read())
        self.write(self.record)
        self.assertEqual(self.read(), self.record)

    def test_record_rejects_stale_identity_and_invalid_endpoint(self) -> None:
        changes = [("protocolVersion", 2), ("protocolVersion", True), ("pid", 124), ("pid", True),
                   ("launchNonce", "cd" * 32), ("launchNonce", self.nonce.upper()),
                   ("controller", "127.0.0.1:0"), ("controller", "127.0.0.1:65536"),
                   ("controller", "127.0.0.1:06500"), ("controller", "localhost:65000"),
                   ("controller", "0.0.0.0:65000"), ("controller", "[::1]:65000"),
                   ("controller", "http://127.0.0.1:65000"), ("coreInstance", ""),
                   ("coreInstance", "instance\nother"), ("coreInstance", "x" * 129)]
        for key, value in changes:
            with self.subTest(key=key, value=value):
                self.write({**self.record, key: value})
                with self.assertRaises(SmokeFailure):
                    self.read()

    def test_record_rejects_oversize_duplicate_or_unknown_fields(self) -> None:
        payloads = [b"", b"\xff", b"[]", b"{" + b" " * 4096,
                    json.dumps({**self.record, "secret": "forbidden"}).encode(),
                    json.dumps(self.record).replace('"pid": 123', '"pid": 123, "pid": 123').encode()]
        for payload in payloads:
            with self.subTest(size=len(payload)):
                self.path.write_bytes(payload)
                with self.assertRaises(SmokeFailure):
                    self.read()
        self.write({key: value for key, value in self.record.items() if key != "launchNonce"})
        with self.assertRaises(SmokeFailure):
            self.read()

    def test_record_rejects_symlink_and_opened_reparse_object(self) -> None:
        self.write(self.record)
        with patch.object(Path, "is_symlink", return_value=True):
            with self.assertRaisesRegex(SmokeFailure, "^upgrade_controller_record_not_regular$"):
                self.read()
        metadata = self.path.stat()
        with patch("upgrade_tun_smoke.os.fstat", return_value=SimpleNamespace(
                st_mode=metadata.st_mode, st_file_attributes=0x400)):
            with self.assertRaisesRegex(SmokeFailure, "^upgrade_controller_record_not_regular$"):
                self.read()

    def test_private_tun_source_never_allocates_unused_mixed_port(self) -> None:
        fixture = object.__new__(TunUpgradeAcceptance)
        fixture.socks = SimpleNamespace(server_address=("127.0.0.1", 23456))
        with patch("upgrade_tun_smoke.free_port", side_effect=AssertionError("unexpected port probe")):
            source = fixture.source("PrivateFixture", mixed=0).decode()
        self.assertIn("mixed-port: 0\n", source)
        self.assertIn("  device: PrivateFixture\n", source)


class TcpOwnerDiscoveryTests(unittest.TestCase):
    @staticmethod
    def table(rows) -> bytes:
        result = bytes(DWORD(len(rows)))
        for pid, address, port in rows:
            result += bytes(TcpOwnerRow(state=2, pid=pid,
                local_address=int.from_bytes(socket.inet_aton(address), sys.byteorder),
                local_port=socket.htons(port)))
        return result

    def test_native_table_filters_exact_pid_and_loopback_with_network_order(self) -> None:
        payload = self.table([(123, "127.0.0.1", 65000), (124, "127.0.0.1", 65001),
                              (123, "198.18.0.1", 65002), (123, "0.0.0.0", 65003)])
        self.assertEqual(decode_loopback_listeners(payload, 123),
                         [{"address": "127.0.0.1", "port": 65000, "pid": 123}])
        self.assertEqual(decode_loopback_listeners(payload, 999), [])

    def test_native_table_rejects_truncation_and_invalid_listener(self) -> None:
        valid = self.table([(123, "127.0.0.1", 65000)])
        for payload in (b"", bytes(DWORD(2)) + valid[4:], valid[:-1],
                        self.table([(123, "127.0.0.1", 0)])):
            with self.subTest(size=len(payload)):
                with self.assertRaises(SmokeFailure):
                    decode_loopback_listeners(payload, 123)

    def test_table_cannot_authorize_a_replaced_or_exited_process(self) -> None:
        expected = SimpleNamespace(pid=123, path="verified-core.exe", created=456)
        payload = self.table([(123, "127.0.0.1", 65000)])
        calls = []
        states = []

        def query(buffer, needed, ordered, family, kind, reserved):
            calls.append((family, kind, reserved))
            ctypes.memmove(buffer, payload, len(payload))
            ctypes.cast(needed, ctypes.POINTER(DWORD)).contents.value = len(payload)
            return 0

        native = object.__new__(NativeTunIdentity)
        native.ip = SimpleNamespace(GetExtendedTcpTable=query)
        processes = SimpleNamespace(
            api=SimpleNamespace(WaitForSingleObject=lambda handle, timeout: states.pop(0)),
            from_handle=lambda handle, pid: expected)
        states[:] = [258, 258]
        self.assertEqual(native.loopback_listeners(expected, 789, processes),
                         [{"address": "127.0.0.1", "port": 65000, "pid": 123}])
        self.assertEqual(calls, [(socket.AF_INET, 3, 0)])
        states[:] = [258, 0]
        with self.assertRaisesRegex(SmokeFailure, "^upgrade_tcp_owner_identity_changed$"):
            native.loopback_listeners(expected, 789, processes)
        states[:] = [0]
        before = len(calls)
        with self.assertRaisesRegex(SmokeFailure, "^upgrade_tcp_owner_identity_changed$"):
            native.loopback_listeners(expected, 789, processes)
        self.assertEqual(len(calls), before)
        states[:] = [258]
        processes.from_handle = lambda handle, pid: SimpleNamespace(pid=123, path=expected.path, created=999)
        with self.assertRaisesRegex(SmokeFailure, "^upgrade_tcp_owner_identity_changed$"):
            native.loopback_listeners(expected, 789, processes)
        self.assertEqual(len(calls), before)


if __name__ == "__main__":
    unittest.main()
