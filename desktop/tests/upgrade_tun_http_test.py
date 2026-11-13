"""Controller response contracts for the Windows upgrade fixture, runnable on any OS."""

from __future__ import annotations

import http.server
import json
import threading
import unittest

from upgrade_tun_smoke import TunUpgradeAcceptance
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
        for path in ("/desktop", "/configs"):
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
                                    ("GET", "/configs", (201, 202, 204, 403, 500))):
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


if __name__ == "__main__":
    unittest.main()
