"""Allocate loopback ports usable by both halves of a mixed/SOCKS listener."""

from __future__ import annotations

import contextlib
import errno
import socket


def available_ports(count: int) -> list[int]:
    # Let UDP choose first: a TCP ephemeral port can be excluded for UDP on
    # Windows. Hold every accepted pair until the whole batch is distinct.
    # Reservations end on return; the real readiness checks still guard the
    # handoff to the core instead of retrying or skipping a product failure.
    ports = []
    with contextlib.ExitStack() as reservations:
        for _ in range(count):
            for _attempt in range(64):
                with contextlib.ExitStack() as candidate:
                    udp = candidate.enter_context(socket.socket(socket.AF_INET, socket.SOCK_DGRAM))
                    tcp = candidate.enter_context(socket.socket(socket.AF_INET, socket.SOCK_STREAM))
                    if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
                        for listener in (udp, tcp):
                            listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                    udp.bind(("127.0.0.1", 0))
                    port = udp.getsockname()[1]
                    try:
                        tcp.bind(("127.0.0.1", port))
                    except OSError as error:
                        if error.errno not in (errno.EADDRINUSE, errno.EACCES, 10048, 10013) and getattr(
                                error, "winerror", None) not in (10048, 10013):
                            raise
                        continue
                    reservations.enter_context(candidate.pop_all())
                    ports.append(port)
                    break
            else:
                raise RuntimeError("no_available_tcp_udp_fixture_port")
    return ports


def free_port() -> int:
    return available_ports(1)[0]
