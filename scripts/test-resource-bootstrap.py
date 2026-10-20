#!/usr/bin/env python3
"""Test startup resource downloads with a real core and loopback-only fixtures.

Usage: python3 scripts/test-resource-bootstrap.py --core /absolute/path/to/mihomo
No TUN, system proxy, public DNS, or public resource server is used. These tests
run main, not -t: ordinary rule providers initialize during ApplyConfig.
"""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import http.client
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
import tempfile
import threading
import time


RULES = b"payload:\n  - '+.resource-bootstrap.invalid'\n"
CACHE = Path("rules/bootstrap.yaml")
LATE_CACHE = Path("rules/late.yaml")
CLI_BUDGET = "1s"
QUERY_BUDGET = "350ms"
FAILURE_TIMEOUT_SECONDS = 5.0


class Failure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise Failure(message)


def digest(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def child_environment(secret: str) -> dict[str, str]:
    allowed = {"SYSTEMROOT", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP",
               "TMPDIR", "SYSTEMDRIVE", "COMSPEC", "TZ"}
    environment = {key: value for key, value in os.environ.items()
                   if key.upper() in allowed}
    environment["CLASH_OVERRIDE_SECRET"] = secret
    return environment


def free_port(excluded: set[int]) -> int:
    for _ in range(20):
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
            if port not in excluded:
                return port
    raise Failure("could not reserve a distinct controller port")


def require_unreachable(port: int) -> None:
    try:
        connection = socket.create_connection(("127.0.0.1", port), timeout=0.5)
    except OSError:
        return
    connection.close()
    raise Failure("resource fixture was unexpectedly reachable without SOCKS")


def receive_exact(connection: socket.socket, count: int) -> bytes:
    result = bytearray()
    while len(result) < count:
        part = connection.recv(count - len(result))
        if not part:
            raise Failure("fixture connection ended before its request")
        result.extend(part)
    return bytes(result)


class ResourceProxy(socketserver.ThreadingTCPServer):
    """A real SOCKS5 handshake, followed by a controlled HTTP resource reply.

    The destination is a held, non-listening loopback port. The fixture never
    forwards to it or resolves a hostname, so only this proxy can serve its URL.
    """

    daemon_threads = True
    block_on_close = False

    def __init__(self, target_port: int, *, delayed: bool = False,
                 health_delay: float = 0, fail_resources: bool = False) -> None:
        super().__init__(("127.0.0.1", 0), ResourceHandler)
        self.target_port = target_port
        self.started = threading.Event()
        self.finished = threading.Event()
        self.release = threading.Event()
        self.closing = threading.Event()
        self.health_delay = health_delay
        self.fail_resources = fail_resources
        if not delayed:
            self.release.set()
        self.condition = threading.Condition()
        self.connections: set[socket.socket] = set()
        self.requests = 0
        self.health_requests = 0
        self.errors: list[str] = []
        self.thread = threading.Thread(target=self.serve_forever,
                                       kwargs={"poll_interval": 0.05}, daemon=True)

    @property
    def port(self) -> int:
        return self.server_address[1]

    def __enter__(self) -> ResourceProxy:
        self.thread.start()
        return self

    def __exit__(self, *_exc: object) -> None:
        self.closing.set()
        self.release.set()
        self.shutdown()
        with self.condition:
            connections = list(self.connections)
        for connection in connections:
            with contextlib.suppress(OSError):
                connection.shutdown(socket.SHUT_RDWR)
        self.server_close()
        self.thread.join(timeout=2)
        with self.condition:
            stopped = self.condition.wait_for(lambda: not self.connections, timeout=2)
        require(stopped and not self.thread.is_alive(), "resource fixture did not stop")

    def assert_requested(self) -> None:
        require(self.started.is_set() and self.requests > 0,
                "core never sent a resource request through the selected SOCKS node")
        require(not self.errors, "resource fixture rejected its SOCKS or HTTP request")

    def finish_late_response(self) -> None:
        self.release.set()
        require(self.finished.wait(2), "delayed resource response did not finish")
        self.assert_requested()


class ResourceHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        proxy: ResourceProxy = self.server
        connection: socket.socket = self.request
        requested = False
        with proxy.condition:
            proxy.connections.add(connection)
        try:
            connection.settimeout(3)
            version, count = receive_exact(connection, 2)
            require(version == 5 and 0 in receive_exact(connection, count),
                    "unexpected SOCKS authentication")
            connection.sendall(b"\x05\x00")
            require(receive_exact(connection, 4) == b"\x05\x01\x00\x01",
                    "expected SOCKS CONNECT to an IPv4 literal")
            host = socket.inet_ntoa(receive_exact(connection, 4))
            port = struct.unpack("!H", receive_exact(connection, 2))[0]
            require((host, port) == ("127.0.0.1", proxy.target_port),
                    "SOCKS attempted a destination outside the fixture")
            connection.sendall(b"\x05\x00\x00\x01\x7f\x00\x00\x01\x00\x00")
            headers = bytearray()
            while b"\r\n\r\n" not in headers and len(headers) <= 16384:
                headers.extend(receive_exact(connection, 1))
            first_line = bytes(headers).split(b"\r\n", 1)[0]
            require(len(headers) <= 16384, "resource HTTP headers exceeded fixture limit")
            if first_line in (b"GET /health HTTP/1.1", b"HEAD /health HTTP/1.1"):
                with proxy.condition:
                    proxy.health_requests += 1
                proxy.closing.wait(proxy.health_delay)
                with contextlib.suppress(OSError):
                    connection.sendall(b"HTTP/1.1 204 No Content\r\n"
                                       b"Connection: close\r\nContent-Length: 0\r\n\r\n")
                return
            require(first_line == b"GET /rules.yaml HTTP/1.1",
                    "unexpected resource HTTP request")
            with proxy.condition:
                proxy.requests += 1
            requested = True
            proxy.started.set()
            require(proxy.release.wait(10), "test did not release its delayed response")
            if proxy.fail_resources:
                response = (b"HTTP/1.1 503 Service Unavailable\r\n"
                            b"Connection: close\r\nContent-Length: 0\r\n\r\n")
            else:
                response = (b"HTTP/1.1 200 OK\r\nContent-Type: application/yaml\r\n"
                            b"Connection: close\r\nContent-Length: "
                            + str(len(RULES)).encode() + b"\r\n\r\n" + RULES)
            # A cancelled download may close its socket before the deliberately
            # late response. That is expected; protocol failures above are not.
            with contextlib.suppress(OSError):
                connection.sendall(response)
        except (Failure, OSError) as error:
            with proxy.condition:
                proxy.errors.append(type(error).__name__)
        finally:
            if requested:
                proxy.finished.set()
            with proxy.condition:
                proxy.connections.discard(connection)
                proxy.condition.notify_all()


def configuration(proxy_port: int, target_port: int) -> dict:
    return {
        "resource-bootstrap": {"proxy": "bootstrap-node", "timeout": 10},
        "mixed-port": 0, "port": 0, "socks-port": 0,
        "allow-lan": False, "bind-address": "127.0.0.1", "mode": "rule",
        "log-level": "warning", "ipv6": False, "find-process-mode": "off",
        "dns": {"enable": False}, "tun": {"enable": False},
        "ntp": {"enable": False},
        "profile": {"store-selected": False, "store-fake-ip": False},
        "geo-auto-update": False, "core-auto-update": False,
        "external-ui-auto-update": False,
        "proxies": [{"name": "bootstrap-node", "type": "socks5",
                     "server": "127.0.0.1", "port": proxy_port, "udp": False}],
        "rule-providers": {
            "bootstrap-rules": {
                "type": "http", "behavior": "domain", "format": "yaml",
                "url": f"http://127.0.0.1:{target_port}/rules.yaml",
                "path": "./" + CACHE.as_posix(), "interval": 0,
            },
        },
        "rules": ["RULE-SET,bootstrap-rules,REJECT", "MATCH,DIRECT"],
    }


class Core:
    def __init__(self, executable: Path, home: Path, config: dict,
                 *, budget: str | None = None, timeout: float = 15,
                 environment: dict[str, str] | None = None) -> None:
        self.home, self.timeout = home, timeout
        home.mkdir(parents=True, exist_ok=True)
        # JSON is valid YAML and keeps this test independent of PyYAML.
        (home / "config.yaml").write_text(json.dumps(config), encoding="utf-8")
        proxy_ports = {node["port"] for node in config.get("proxies", [])
                       if isinstance(node.get("port"), int)}
        self.port = free_port(proxy_ports)
        self.secret = secrets.token_urlsafe(32)
        self.log = tempfile.TemporaryFile()
        arguments = [str(executable), "-desktop-managed", "-d", str(home),
                     "-f", str(home / "config.yaml"),
                     "-ext-ctl", f"127.0.0.1:{self.port}"]
        if budget is not None:
            arguments.append("-resource-bootstrap-timeout=" + budget)
        child_env = child_environment(self.secret)
        if environment:
            child_env.update(environment)
        try:
            self.process = subprocess.Popen(
                arguments, cwd=home, env=child_env,
                stdin=subprocess.DEVNULL, stdout=self.log, stderr=subprocess.STDOUT,
            )
        except BaseException:
            self.log.close()
            raise

    def __enter__(self) -> Core:
        return self

    def request(self, method: str, path: str, body: object = None,
                *, timeout: float = 2) -> tuple[int, dict]:
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
        headers = {"Authorization": "Bearer " + self.secret, "Connection": "close"}
        payload = None if body is None else json.dumps(body).encode()
        if payload is not None:
            headers["Content-Type"] = "application/json"
        try:
            connection.request(method, path, body=payload, headers=headers)
            response = connection.getresponse()
            data = response.read(1024 * 1024 + 1)
            require(len(data) <= 1024 * 1024, "controller response exceeded test limit")
            require(self.secret.encode() not in data, "controller exposed its runtime secret")
            return response.status, json.loads(data) if data else {}
        finally:
            connection.close()

    def status(self) -> dict:
        status, data = self.request("GET", "/desktop")
        require(status == 200 and data.get("managed") is True
                and data.get("api_version") == 1 and data.get("pid") == self.process.pid,
                "controller does not belong to the expected managed core")
        return data

    def diagnostics(self) -> list[dict]:
        self.log.seek(0, os.SEEK_END)
        self.log.seek(max(0, self.log.tell() - 16384))
        text = self.log.read().decode("utf-8", errors="replace")
        # Never print raw configuration, URLs, environment, or protocol errors.
        return [{"code": code, "kind": kind} for code, kind in
                re.findall(r"\[resource-bootstrap\] code=([a-z_]+) kind=([a-z_]+)", text)][-8:]

    def wait_ready(self) -> None:
        deadline = time.monotonic() + self.timeout
        while time.monotonic() < deadline:
            require(self.process.poll() is None,
                    "core exited before readiness: " + json.dumps(self.diagnostics()))
            try:
                if self.status().get("state") == "running":
                    return
            except (OSError, http.client.HTTPException):
                pass
            time.sleep(0.03)
        raise Failure("core did not become ready: " + json.dumps(self.diagnostics()))

    def assert_rule_loaded(self) -> None:
        status, data = self.request("GET", "/providers/rules")
        provider = data.get("providers", {}).get("bootstrap-rules", {})
        require(status == 200 and provider.get("ruleCount") == 1,
                "main did not initialize the downloaded rule provider")
        require((self.home / CACHE).read_bytes() == RULES,
                "rule cache did not use the configured path or expected contents")

    def __exit__(self, *_exc: object) -> None:
        try:
            if self.process.poll() is None:
                with contextlib.suppress(OSError, http.client.HTTPException, Failure):
                    self.request("POST", "/desktop/shutdown", timeout=1)
                try:
                    self.process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    self.process.terminate()
                    try:
                        self.process.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        self.process.kill()
                        self.process.wait(timeout=3)
        finally:
            self.log.close()


def group_configuration(kind: str, nodes: list[dict], target_port: int) -> dict:
    config = configuration(1, target_port)
    config["proxies"] = nodes
    config["resource-bootstrap"]["proxy"] = "Bootstrap"
    config["proxy-groups"] = [{
        "name": "Bootstrap", "type": kind,
        "proxies": [node["name"] for node in nodes],
        "url": f"http://127.0.0.1:{target_port}/health",
        "expected-status": "204", "timeout": 500,
        "interval": 300, "lazy": True,
    }]
    return config


def socks_node(name: str, port: int) -> dict:
    return {"name": name, "type": "socks5", "server": "127.0.0.1",
            "port": port, "udp": False}


def run_groups(executable: Path, root: Path, target_port: int,
               timeout: float, report: dict, passed) -> None:
    with socket.socket() as unavailable:
        unavailable.bind(("127.0.0.1", 0))
        unavailable_port = unavailable.getsockname()[1]
        require_unreachable(unavailable_port)

        for kind in ("select", "fallback", "url-test", "load-balance"):
            report["stage"] = "group_" + kind.replace("-", "_") + "_cold_start"
            home = root / kind
            with ResourceProxy(target_port, health_delay=0.03) as fast, \
                    ResourceProxy(target_port, health_delay=0.18) as slow:
                nodes = [socks_node("slow", slow.port), socks_node("fast", fast.port)]
                if kind in ("select", "fallback"):
                    nodes[0] = socks_node("unavailable", unavailable_port)
                config = group_configuration(kind, nodes, target_port)
                group = config["proxy-groups"][0]
                if kind == "select":
                    group["default-selected"] = "fast"
                if kind == "load-balance":
                    group["strategy"] = "round-robin"
                    config["rule-providers"]["second-rules"] = {
                        **config["rule-providers"]["bootstrap-rules"],
                        "path": "./rules/second.yaml",
                    }
                    config["rules"].insert(0, "RULE-SET,second-rules,REJECT")
                with Core(executable, home, config, timeout=timeout) as core:
                    core.wait_ready()
                    core.assert_rule_loaded()
                    fast.assert_requested()
                    if kind == "load-balance":
                        slow.assert_requested()
                        require((home / "rules/second.yaml").read_bytes() == RULES,
                                "load-balance did not load both resource files")
                        require(fast.requests == slow.requests == 1,
                                "round-robin did not preserve group selection")
                    else:
                        require(fast.requests == 1 and slow.requests == 0,
                                "group did not choose its expected working member")
                    if kind == "url-test":
                        require(fast.health_requests > 0 and slow.health_requests > 0,
                                "url-test selected without measuring both candidates")
                    passed(report["stage"], fast_requests=fast.requests,
                           slow_requests=slow.requests, cache_loaded=True)

            if kind == "fallback":
                report["stage"] = "group_offline_cache_start"
                require_unreachable(fast.port)
                with Core(executable, home, config, timeout=timeout) as core:
                    core.wait_ready()
                    core.assert_rule_loaded()
                    passed(report["stage"], group_members_offline=True, cache_loaded=True)

        report["stage"] = "group_http_failure_fallback"
        with ResourceProxy(target_port, fail_resources=True) as failed, \
                ResourceProxy(target_port) as working:
            config = group_configuration("fallback", [
                socks_node("http-failure", failed.port),
                socks_node("working", working.port),
            ], target_port)
            with Core(executable, root / "http-fallback", config, timeout=timeout) as core:
                core.wait_ready()
                core.assert_rule_loaded()
                failed.assert_requested()
                working.assert_requested()
                require(failed.requests == working.requests == 1,
                        "resource HTTP failure did not advance once to the next candidate")
                passed(report["stage"], failed_status=503,
                       failed_requests=failed.requests, working_requests=working.requests)

        report["stage"] = "nested_group_cold_start"
        with ResourceProxy(target_port) as proxy:
            config = group_configuration("fallback", [
                socks_node("unavailable", unavailable_port),
                socks_node("working", proxy.port),
            ], target_port)
            config["proxy-groups"][0]["name"] = "Automatic"
            config["proxy-groups"].append({
                "name": "Bootstrap", "type": "select", "proxies": ["Automatic"],
            })
            with Core(executable, root / "nested", config, timeout=timeout) as core:
                core.wait_ready()
                core.assert_rule_loaded()
                proxy.assert_requested()
                passed(report["stage"], cache_loaded=True)

        report["stage"] = "provider_file_group_cold_start"
        with ResourceProxy(target_port) as proxy:
            home = root / "provider-group"
            (home / "proxies").mkdir(parents=True)
            (home / "proxies/nodes.yaml").write_text(json.dumps({"proxies": [
                socks_node("excluded", unavailable_port),
                socks_node("download-working", proxy.port),
            ]}), encoding="utf-8")
            config = group_configuration("select", [], target_port)
            config["proxy-providers"] = {
                "local-nodes": {"type": "file", "path": "./proxies/nodes.yaml"},
            }
            group = config["proxy-groups"][0]
            group.pop("proxies")
            group.update({"use": ["local-nodes"], "filter": "^download-"})
            with Core(executable, home, config, timeout=timeout) as core:
                core.wait_ready()
                core.assert_rule_loaded()
                proxy.assert_requested()
                passed(report["stage"], provider_cache_only=True, filter_applied=True)

        for default_proxy in ("unavailable", None):
            suffix = "override" if default_proxy else "without_default"
            report["stage"] = "provider_group_" + suffix
            with ResourceProxy(target_port) as proxy:
                config = group_configuration("select", [socks_node("working", proxy.port)], target_port)
                config["proxies"].append(socks_node("unavailable", unavailable_port))
                if default_proxy:
                    config["resource-bootstrap"]["proxy"] = default_proxy
                else:
                    config["resource-bootstrap"].pop("proxy")
                config["rule-providers"]["bootstrap-rules"]["proxy"] = "Bootstrap"
                with Core(executable, root / ("provider-" + suffix), config,
                          timeout=timeout) as core:
                    core.wait_ready()
                    core.assert_rule_loaded()
                    proxy.assert_requested()
                    passed(report["stage"], explicit_provider_group_used=True,
                           default_bootstrap_configured=bool(default_proxy))

        report["stage"] = "select_saved_choice_cold_start"
        with ResourceProxy(target_port) as first, ResourceProxy(target_port) as second:
            home = root / "saved-choice"
            config = group_configuration("select", [
                socks_node("first", first.port), socks_node("second", second.port),
            ], target_port)
            config["profile"]["store-selected"] = True
            with Core(executable, home, config, timeout=timeout) as core:
                core.wait_ready()
                core.assert_rule_loaded()
                first.assert_requested()
                status, _ = core.request("PUT", "/proxies/Bootstrap", {"name": "second"})
                require(status == 204, "could not save normal runtime group selection")
            (home / CACHE).unlink()
            with Core(executable, home, config, timeout=timeout) as core:
                core.wait_ready()
                core.assert_rule_loaded()
                second.assert_requested()
                require(first.requests == second.requests == 1,
                        "bootstrap ignored the existing stored selection")
                passed(report["stage"], saved_choice_reused=True)

        report["stage"] = "group_poisoned_proxy_environment"
        with ResourceProxy(target_port) as proxy:
            config = group_configuration("fallback", [
                socks_node("unavailable", unavailable_port),
                socks_node("working", proxy.port),
            ], target_port)
            poisoned = {name: f"http://127.0.0.1:{unavailable_port}" for name in (
                "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
                "http_proxy", "https_proxy", "all_proxy",
            )}
            poisoned.update({"NO_PROXY": "", "no_proxy": ""})
            with Core(executable, root / "environment", config,
                      timeout=timeout, environment=poisoned) as core:
                core.wait_ready()
                core.assert_rule_loaded()
                proxy.assert_requested()
                # This is resilience with a poisoned child environment. Because
                # the destination is loopback, it does not independently prove
                # whether an HTTP library consulted proxy environment variables.
                passed(report["stage"], cache_loaded=True, poisoned_environment=True)

        report["stage"] = "group_probe_preserves_download_budget"
        with ResourceProxy(target_port, health_delay=2) as slow:
            home = root / "group-budget"
            config = group_configuration("url-test", [socks_node("slow", slow.port)], target_port)
            config["proxy-groups"][0]["timeout"] = 5000
            started = time.monotonic()
            with Core(executable, home, config, budget=CLI_BUDGET, timeout=timeout) as core:
                core.wait_ready()
                core.assert_rule_loaded()
                elapsed = time.monotonic() - started
                require(elapsed < FAILURE_TIMEOUT_SECONDS,
                        "group probing consumed the startup budget")
                require(slow.health_requests > 0 and slow.requests == 1,
                        "a stalled health URL blocked the real resource download")
                passed(report["stage"], budget=CLI_BUDGET,
                       elapsed_seconds=round(elapsed, 3), cache_loaded=True,
                       stalled_probe_did_not_block_download=True)


def run(executable: Path, root: Path, timeout: float, report: dict) -> None:
    def passed(name: str, **evidence: object) -> None:
        report["checks"].append({"name": name, "passed": True, **evidence})
        print("PASS " + name, file=sys.stderr, flush=True)

    # Binding without listen keeps this exact port unavailable to an accidental
    # direct HTTP server throughout every case, on Windows and Unix alike.
    with socket.socket() as destination:
        destination.bind(("127.0.0.1", 0))
        target_port = destination.getsockname()[1]
        require_unreachable(target_port)
        cached_home = root / "cached"
        report["stage"] = "static_proxy_main_start"
        with ResourceProxy(target_port) as proxy:
            original = configuration(proxy.port, target_port)
            with Core(executable, cached_home, original, timeout=timeout) as core:
                core.wait_ready()
                core.assert_rule_loaded()
                proxy.assert_requested()
                require(proxy.requests == 1, "initial provider unexpectedly downloaded more than once")
                passed(report["stage"], proxy_requests=proxy.requests,
                       cache_path=CACHE.as_posix(), cache_sha256=digest(cached_home / CACHE))

        report["stage"] = "offline_cache_main_start"
        require_unreachable(original["proxies"][0]["port"])
        with Core(executable, cached_home, original, timeout=timeout) as core:
            core.wait_ready()
            core.assert_rule_loaded()
            passed(report["stage"], proxy_listener_closed=True, cache_unchanged=True)

        run_groups(executable, root / "groups", target_port, timeout, report, passed)

        report["stage"] = "cli_remaining_budget"
        with ResourceProxy(target_port, delayed=True) as proxy:
            home = root / "cli-timeout"
            started = time.monotonic()
            with Core(executable, home, configuration(proxy.port, target_port),
                      budget=CLI_BUDGET, timeout=timeout) as core:
                require(proxy.started.wait(FAILURE_TIMEOUT_SECONDS),
                        "CLI deadline case did not reach its resource request: "
                        + json.dumps(core.diagnostics()))
                try:
                    code = core.process.wait(timeout=max(0.01, FAILURE_TIMEOUT_SECONDS
                                                        - (time.monotonic() - started)))
                except subprocess.TimeoutExpired as error:
                    raise Failure("CLI remaining budget did not stop startup") from error
                elapsed = time.monotonic() - started
                require(code != 0, "resource timeout incorrectly reported a successful process exit")
                require(elapsed < FAILURE_TIMEOUT_SECONDS, "CLI resource budget was reset or ignored")
                diagnostics = core.diagnostics()
                require(any(item["code"] == "deadline_exceeded" for item in diagnostics),
                        "startup failure did not identify the exhausted resource budget")
                require(not (home / CACHE).exists(), "timed-out startup published a cache")
                proxy.finish_late_response()
                time.sleep(0.2)
                require(not (home / CACHE).exists(), "late startup response wrote a cache")
                passed(report["stage"], budget=CLI_BUDGET, exit_code=code,
                       elapsed_seconds=round(elapsed, 3), late_cache_absent=True,
                       diagnostics=diagnostics)

        report["stage"] = "reload_remaining_budget_and_recovery"
        with ResourceProxy(target_port, delayed=True) as proxy:
            with Core(executable, cached_home, original, timeout=timeout) as core:
                core.wait_ready()
                candidate = configuration(proxy.port, target_port)
                candidate["rule-providers"]["late-rules"] = {
                    **candidate["rule-providers"]["bootstrap-rules"],
                    "path": "./" + LATE_CACHE.as_posix(),
                }
                candidate["rules"].insert(0, "RULE-SET,late-rules,REJECT")
                started = time.monotonic()
                status, data = core.request(
                    "PUT", "/configs?force=true&resource-bootstrap-timeout=" + QUERY_BUDGET,
                    {"payload": json.dumps(candidate)}, timeout=FAILURE_TIMEOUT_SECONDS,
                )
                elapsed = time.monotonic() - started
                proxy.assert_requested()
                require(status == 500 and "deadline_exceeded" in json.dumps(data),
                        "reload did not report its exhausted resource budget")
                require(elapsed < FAILURE_TIMEOUT_SECONDS, "reload ignored its remaining budget")
                require(core.process.poll() is None and core.status().get("state") != "running",
                        "partially applied reload incorrectly reported managed readiness")
                proxy.finish_late_response()
                time.sleep(0.2)
                require(not (cached_home / LATE_CACHE).exists(), "late reload response wrote a cache")
                require((cached_home / CACHE).read_bytes() == RULES,
                        "failed reload changed the previously valid cache")
                # ApplyConfig is not transactional. Explicitly restore the old
                # configuration and verify readiness; do not claim auto-rollback.
                status, _ = core.request("PUT", "/configs?force=true",
                                         {"payload": json.dumps(original)})
                require(status == 204, "cached configuration could not recover the failed reload")
                core.wait_ready()
                core.assert_rule_loaded()
                passed(report["stage"], budget=QUERY_BUDGET,
                       elapsed_seconds=round(elapsed, 3), late_cache_absent=True,
                       same_process_recovered=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core", required=True, type=Path)
    parser.add_argument("--timeout", type=float, default=15,
                        help="maximum seconds for normal managed readiness (default: 15)")
    parser.add_argument("--report", type=Path, help="also save the JSON result at this path")
    arguments = parser.parse_args()
    report: dict = {"passed": False, "checks": [], "stage": "setup"}
    result = 1
    try:
        executable = arguments.core.resolve(strict=True)
        require(executable.is_file(), "--core must name a compiled executable")
        require(0 < arguments.timeout <= 120, "--timeout must be between 0 and 120 seconds")
        report["core_sha256"] = digest(executable)
        with tempfile.TemporaryDirectory(prefix="mihomo-resource-bootstrap-") as directory:
            run(executable, Path(directory).resolve(), arguments.timeout, report)
        report["passed"] = True
        report["stage"] = "complete"
        result = 0
    except (Failure, OSError, ValueError, http.client.HTTPException,
            subprocess.SubprocessError) as error:
        report["error"] = str(error)
        print("FAIL " + report["stage"] + ": " + str(error), file=sys.stderr, flush=True)
    finally:
        rendered = json.dumps(report, indent=2, sort_keys=True) + "\n"
        if arguments.report is not None:
            arguments.report.parent.mkdir(parents=True, exist_ok=True)
            arguments.report.write_text(rendered, encoding="utf-8")
        print(rendered, end="", flush=True)
    return result


if __name__ == "__main__":
    raise SystemExit(main())
