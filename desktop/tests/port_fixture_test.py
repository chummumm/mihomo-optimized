"""Deterministic socket-allocation contracts; no native runner is required."""

from __future__ import annotations

import errno
import socket
import unittest
from unittest.mock import patch

import port_fixture


class ReusingPortAllocator:
    """Model an OS that always reuses the first unreserved UDP port."""

    first_port = 50000

    def __init__(self, tcp_errors=None, udp_error=None):
        self.tcp_errors = tcp_errors or {}
        self.udp_error = udp_error
        self.sockets = []
        self.tcp_attempts = []

    def socket(self, family, kind):
        if family != socket.AF_INET:
            raise AssertionError("fixture must probe IPv4 loopback")
        candidate = FixtureSocket(self, kind)
        self.sockets.append(candidate)
        return candidate

    def first_unreserved_udp_port(self):
        reserved = {candidate.port for candidate in self.sockets
                    if candidate.kind == socket.SOCK_DGRAM and not candidate.closed}
        port = self.first_port
        while port in reserved:
            port += 1
        return port


class FixtureSocket:
    def __init__(self, allocator, kind):
        self.allocator = allocator
        self.kind = kind
        self.port = None
        self.closed = False

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.closed = True

    def setsockopt(self, *args):
        pass

    def bind(self, address):
        host, port = address
        if host != "127.0.0.1":
            raise AssertionError("fixture must bind loopback")
        if self.kind == socket.SOCK_DGRAM:
            if self.allocator.udp_error is not None:
                raise self.allocator.udp_error
            if port != 0:
                raise AssertionError("UDP must select the candidate")
            port = self.allocator.first_unreserved_udp_port()
        else:
            self.allocator.tcp_attempts.append(port)
            error = self.allocator.tcp_errors.get(port)
            if error is not None:
                raise error
        self.port = port

    def getsockname(self):
        return "127.0.0.1", self.port


class PortFixtureTests(unittest.TestCase):
    def assert_sockets_closed(self, allocator):
        self.assertTrue(all(candidate.closed for candidate in allocator.sockets),
                        "fixture leaked a successful or rejected reservation")

    def test_rejected_candidate_is_not_immediately_reused(self):
        errors = [OSError(code, "fixture TCP conflict")
                  for code in (errno.EADDRINUSE, errno.EACCES, 10048, 10013)]
        for winerror in (10048, 10013):
            error = OSError(errno.EIO, "fixture Windows TCP conflict")
            error.winerror = winerror
            errors.append(error)
        for error in errors:
            with self.subTest(errno=error.errno, winerror=getattr(error, "winerror", None)):
                allocator = ReusingPortAllocator({50000: error})
                with patch("port_fixture.socket.socket", allocator.socket):
                    port = port_fixture.free_port()
                self.assertEqual(port, 50001)
                self.assertEqual(allocator.tcp_attempts, [50000, 50001])
                self.assert_sockets_closed(allocator)

    def test_batch_keeps_successful_and_rejected_candidates_distinct(self):
        allocator = ReusingPortAllocator({
            50000: OSError(errno.EADDRINUSE, "occupied"),
            50002: OSError(errno.EACCES, "excluded"),
        })
        with patch("port_fixture.socket.socket", allocator.socket):
            ports = port_fixture.available_ports(3)
        self.assertEqual(ports, [50001, 50003, 50004])
        self.assertEqual(allocator.tcp_attempts, [50000, 50001, 50002, 50003, 50004])
        self.assert_sockets_closed(allocator)

    def test_exhaustion_keeps_the_existing_64_attempt_limit_and_releases_all(self):
        candidates = list(range(50000, 50064))
        allocator = ReusingPortAllocator({port: OSError(errno.EADDRINUSE, "occupied")
                                         for port in candidates})
        with patch("port_fixture.socket.socket", allocator.socket):
            with self.assertRaisesRegex(RuntimeError, "^no_available_tcp_udp_fixture_port$"):
                port_fixture.available_ports(1)
        self.assertEqual(allocator.tcp_attempts, candidates)
        self.assert_sockets_closed(allocator)

    def test_unexpected_tcp_error_propagates_and_releases_the_entire_batch(self):
        unexpected = OSError(errno.EIO, "unexpected fixture socket failure")
        allocator = ReusingPortAllocator({
            50000: OSError(errno.EADDRINUSE, "occupied"),
            50002: unexpected,
        })
        with patch("port_fixture.socket.socket", allocator.socket):
            with self.assertRaises(OSError) as raised:
                port_fixture.available_ports(2)
        self.assertIs(raised.exception, unexpected)
        self.assertEqual(allocator.tcp_attempts, [50000, 50001, 50002])
        self.assert_sockets_closed(allocator)

    def test_udp_failure_is_not_retried_and_releases_candidate_sockets(self):
        failure = OSError(errno.EACCES, "UDP allocation failed")
        allocator = ReusingPortAllocator(udp_error=failure)
        with patch("port_fixture.socket.socket", allocator.socket):
            with self.assertRaises(OSError) as raised:
                port_fixture.free_port()
        self.assertIs(raised.exception, failure)
        self.assertEqual(allocator.tcp_attempts, [])
        self.assertEqual(len(allocator.sockets), 2)
        self.assert_sockets_closed(allocator)


if __name__ == "__main__":
    unittest.main()
