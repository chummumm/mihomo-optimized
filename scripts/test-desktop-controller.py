#!/usr/bin/env python3
"""Exercise managed controller discovery with real core processes.

Usage: python3 scripts/test-desktop-controller.py --core /path/to/mihomo
--require-tun adds the Windows test for TUN cleanup after controller bind failure.
No port is probed and released for a later core bind.
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
import stat
import subprocess
import sys
import tempfile
import time


RECORD = "desktop-controller.json"
NONCE_ENV = "MIHOMO_DESKTOP_LAUNCH_NONCE"
FIELDS = {"protocolVersion", "pid", "controller", "coreInstance", "launchNonce"}


class Failure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise Failure(message)


def child_environment(secret: str, nonce: str | None) -> dict[str, str]:
    allowed = {"SYSTEMROOT", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP",
               "TMPDIR", "SYSTEMDRIVE", "COMSPEC", "TZ"}
    environment = {key: value for key, value in os.environ.items()
                   if key.upper() in allowed}
    environment["CLASH_OVERRIDE_SECRET"] = secret
    if nonce is not None:
        environment[NONCE_ENV] = nonce
    return environment


def configuration(tun_name: str | None = None) -> dict:
    tun = {"enable": False}
    if tun_name is not None:
        tun = {"enable": True, "device": tun_name, "stack": "mixed",
               "auto-route": False, "auto-detect-interface": False,
               "dns-hijack": []}
    return {
        "external-controller": "127.0.0.1:0",
        "mixed-port": 0, "port": 0, "socks-port": 0,
        "allow-lan": False, "bind-address": "127.0.0.1", "mode": "direct",
        "log-level": "info", "ipv6": False, "find-process-mode": "off",
        "dns": {"enable": False, "fake-ip-range": "198.18.0.1/16"},
        "tun": tun, "ntp": {"enable": False},
        "profile": {"store-selected": False, "store-fake-ip": False},
        "geo-auto-update": False, "core-auto-update": False,
        "external-ui-auto-update": False, "rules": ["MATCH,DIRECT"],
    }


def unique_fields(items: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in items:
        require(key not in result, "discovery record contains duplicate fields")
        result[key] = value
    return result


def read_record(path: Path) -> tuple[bytes, dict]:
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and not path.is_symlink()
            and not (getattr(info, "st_file_attributes", 0) & 0x400),
            "discovery record is not a regular file")
    require(info.st_size <= 4096, "discovery record exceeds 4 KiB")
    with path.open("rb") as source:
        data = source.read(4097)
    require(len(data) <= 4096, "discovery record grew beyond 4 KiB")
    record = json.loads(data, object_pairs_hook=unique_fields)
    require(type(record) is dict and set(record) == FIELDS,
            "discovery record schema changed")
    require(type(record["protocolVersion"]) is int and record["protocolVersion"] == 1,
            "unsupported discovery protocol")
    require(type(record["pid"]) is int and record["pid"] > 0,
            "invalid discovery process identity")
    require(type(record["controller"]) is str
            and re.fullmatch(r"127\.0\.0\.1:[1-9][0-9]{0,4}", record["controller"]) is not None
            and int(record["controller"].rsplit(":", 1)[1]) <= 65535,
            "invalid discovery controller")
    require(type(record["coreInstance"]) is str
            and re.fullmatch(r"[A-Za-z0-9_-]{1,128}", record["coreInstance"]) is not None,
            "invalid discovery core instance")
    require(type(record["launchNonce"]) is str
            and re.fullmatch(r"[0-9a-f]{64}", record["launchNonce"]) is not None,
            "invalid discovery launch identity")
    return data, record


class Core:
    def __init__(self, executable: Path, home: Path, *, timeout: float,
                 controller: str = "127.0.0.1:0", tun_name: str | None = None) -> None:
        self.executable, self.home, self.timeout = executable, home, timeout
        self.nonce, self.secret = secrets.token_hex(32), secrets.token_urlsafe(32)
        self.record: dict | None = None
        self.address: str | None = None
        self.document = configuration(tun_name)
        home.mkdir(parents=True, exist_ok=True)
        self.config_path = home / "config.yaml"
        self.config_path.write_text(json.dumps(self.document), encoding="utf-8")
        self.output = tempfile.NamedTemporaryFile(dir=home, prefix="controller-fixture-", suffix=".log", delete=False)
        self.log_path = Path(self.output.name)
        self.started = time.monotonic()
        try:
            self.process = subprocess.Popen(
                [str(executable), "-desktop-managed", "-d", str(home),
                 "-f", str(self.config_path), "-ext-ctl", controller],
                cwd=home, env=child_environment(self.secret, self.nonce),
                stdin=subprocess.DEVNULL, stdout=self.output, stderr=subprocess.STDOUT,
                close_fds=True,
            )
        except BaseException:
            self.output.close()
            raise

    def __enter__(self) -> Core:
        return self

    def __exit__(self, *_exc: object) -> None:
        try:
            if self.process.poll() is None:
                if self.address is not None:
                    with contextlib.suppress(Exception):
                        self.request("POST", "/desktop/shutdown")
                try:
                    self.process.wait(timeout=self.timeout)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=self.timeout)
        finally:
            self.output.close()

    def log_tail(self) -> str:
        with self.log_path.open("rb") as source:
            source.seek(0, os.SEEK_END)
            end = source.tell()
            source.seek(max(0, end - 8192))
            result = source.read().decode("utf-8", errors="replace")
        return result.replace(self.secret, "<redacted>").replace(self.nonce, "<redacted>")

    def request(self, method: str, path: str, body: object = None,
                *, timeout: float = 3) -> tuple[int, dict]:
        require(self.address is not None, "controller request preceded discovery")
        port = int(self.address.rsplit(":", 1)[1])
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=timeout)
        payload = None if body is None else json.dumps(body).encode()
        headers = {"Authorization": "Bearer " + self.secret, "Connection": "close"}
        if payload is not None:
            headers["Content-Type"] = "application/json"
        try:
            connection.request(method, path, body=payload, headers=headers)
            response = connection.getresponse()
            data = response.read(1024 * 1024 + 1)
            require(len(data) <= 1024 * 1024, "controller response exceeded limit")
            require(self.secret.encode() not in data, "controller exposed its credential")
            return response.status, json.loads(data) if data else {}
        finally:
            connection.close()

    def verify_status(self, status: dict) -> None:
        require(self.record is not None and self.process.poll() is None,
                "desktop identity was checked without a live owned process")
        require(status.get("managed") is True and status.get("api_version") == 1
                and status.get("pid") == self.process.pid
                and status.get("core_instance") == self.record["coreInstance"]
                and status.get("controller") == self.record["controller"],
                "authenticated desktop identity disagreed with discovery")

    def ready(self) -> dict:
        deadline = self.started + self.timeout
        last = "discovery absent"
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise Failure("core exited before readiness:\n" + self.log_tail())
            try:
                _data, record = read_record(self.home / RECORD)
                if record["launchNonce"] != self.nonce or record["pid"] != self.process.pid:
                    last = "stale discovery record"
                    time.sleep(0.02)
                    continue
                self.record, self.address = record, record["controller"]
                code, status = self.request("GET", "/desktop", timeout=1)
                require(code == 200, "authenticated desktop request was rejected")
                self.verify_status(status)
                if status.get("state") == "running":
                    return status
                last = "desktop is loading"
            except FileNotFoundError:
                last = "discovery absent"
            except (ConnectionError, TimeoutError, http.client.HTTPException):
                last = "bound controller has not served desktop status"
            time.sleep(0.02)
        raise Failure("managed readiness timed out (" + last + "):\n" + self.log_tail())

    def shutdown(self) -> None:
        code, body = self.request("POST", "/desktop/shutdown")
        require(code == 202 and body.get("status") == "stopping",
                "shutdown did not acknowledge the owned process")
        require(self.process.wait(timeout=self.timeout) == 0,
                "graceful managed shutdown failed:\n" + self.log_tail())
        require(not (self.home / RECORD).exists(), "graceful exit retained its discovery record")

    def expect_start_failure(self, marker: str) -> float:
        try:
            result = self.process.wait(timeout=self.timeout)
        except subprocess.TimeoutExpired as error:
            raise Failure("startup failure left the owned process alive:\n" + self.log_tail()) from error
        elapsed = time.monotonic() - self.started
        require(result != 0 and marker in self.log_tail(),
                "startup did not report the expected fatal controller failure:\n" + self.log_tail())
        return elapsed


@contextlib.contextmanager
def occupied_controller():
    holder = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        if os.name == "nt":
            holder.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        holder.bind(("127.0.0.1", 0))
        holder.listen()
        yield f"127.0.0.1:{holder.getsockname()[1]}"
    finally:
        holder.close()


def run(executable: Path, root: Path, timeout: float, report: dict, require_tun: bool) -> None:
    report["stage"] = "concurrent_ephemeral_controllers"
    with contextlib.ExitStack() as stack:
        # All children are launched before any readiness check: the OS must
        # select and retain three independent ports under concurrent startup.
        cores = [stack.enter_context(Core(executable, root / str(index), timeout=timeout))
                 for index in range(3)]
        for core in cores:
            core.ready()
        addresses = {core.address for core in cores}
        require(len(addresses) == len(cores), "live managed controllers shared a port")
        report["controller_ports"] = sorted(int(address.rsplit(":", 1)[1]) for address in addresses)
        report["checks"].append("three_concurrent_os_bound_ports")
        report["stage"] = "reload_and_validation_keep_controller"
        core = cores[0]
        original, _record = read_record(core.home / RECORD)
        for address in ("127.0.0.1:0", core.address):
            document = dict(core.document)
            document["external-controller"] = address
            document["mode"] = "rule"
            core.config_path.write_text(json.dumps(document), encoding="utf-8")
            code, _body = core.request("PUT", "/configs?force=true", {"path": str(core.config_path)},
                                       timeout=timeout)
            require(code == 204, "full configuration reload failed")
            code, status = core.request("GET", "/desktop")
            require(code == 200 and status.get("state") == "running", "reload lost managed readiness")
            core.verify_status(status)
            code, applied = core.request("GET", "/configs")
            require(code == 200 and applied.get("mode") == "rule", "reload did not apply its configuration")
            after, _record = read_record(core.home / RECORD)
            require(after == original, "reload replaced the controller discovery identity")
        report["checks"].append("zero_and_actual_address_reload_preserve_pid_instance_port")
        validation = subprocess.run(
            [str(executable), "-desktop-managed", "-t", "-d", str(core.home),
             "-f", str(core.config_path), "-ext-ctl", core.address],
            cwd=core.home, env=child_environment(core.secret, None),
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=timeout, check=False,
        )
        require(validation.returncode == 0, "configuration validation child failed")
        after, _record = read_record(core.home / RECORD)
        require(after == original, "configuration validation changed a live discovery record")
        code, status = core.request("GET", "/desktop")
        require(code == 200, "validation disrupted the live controller")
        core.verify_status(status)
        report["checks"].append("validation_child_does_not_publish_or_remove_record")
        for core in cores:
            core.shutdown()
        report["checks"].append("graceful_exit_removes_owned_record")

    report["stage"] = "stale_discovery"
    home = root / "stale"
    home.mkdir()
    (home / RECORD).write_text(json.dumps({
        "protocolVersion": 1, "pid": os.getpid(), "controller": "127.0.0.1:1",
        "coreInstance": "stale", "launchNonce": secrets.token_hex(32),
    }), encoding="utf-8")
    with Core(executable, home, timeout=timeout) as core:
        core.ready()
        require(core.record["coreInstance"] != "stale", "stale discovery was accepted")
        core.shutdown()
    report["checks"].append("new_launch_replaces_stale_record")

    report["stage"] = "controller_bind_failure"
    with occupied_controller() as address:
        with Core(executable, root / "occupied", timeout=timeout, controller=address) as core:
            report["bind_failure_exit_seconds"] = round(core.expect_start_failure("desktop controller listen"), 3)
            require(not (core.home / RECORD).exists(), "failed bind published discovery")
    report["checks"].append("occupied_controller_is_fatal_without_readiness_timeout")

    report["stage"] = "discovery_publication_failure"
    home = root / "unpublishable"
    home.mkdir()
    (home / RECORD).mkdir()
    with Core(executable, home, timeout=timeout) as core:
        report["publication_failure_exit_seconds"] = round(
            core.expect_start_failure("desktop controller discovery"), 3)
        require((home / RECORD).is_dir(), "publication failure modified an unowned destination")
    (home / RECORD).rmdir()
    with Core(executable, home, timeout=timeout) as core:
        core.ready()
        core.shutdown()
    report["checks"].append("publication_failure_is_fatal_and_next_launch_succeeds")

    if require_tun:
        require(os.name == "nt", "--require-tun is a Windows native acceptance test")
        report["stage"] = "tun_cleanup_after_controller_failure"
        name = "MihomoControl-" + secrets.token_hex(6)
        home = root / "tun-controller-failure"
        with occupied_controller() as address:
            with Core(executable, home, timeout=timeout, controller=address, tun_name=name) as failed:
                report["tun_bind_failure_exit_seconds"] = round(
                    failed.expect_start_failure("desktop controller listen"), 3)
                require("Tun adapter listening" in failed.log_tail(),
                        "controller failure did not occur after a real TUN had been created")
                # This is the very next process operation after confirmed exit:
                # no sleep, adapter polling, repair, rename or failure retry.
                with Core(executable, home, timeout=timeout, tun_name=name) as recovered:
                    recovered.ready()
                    code, applied = recovered.request("GET", "/configs")
                    tun = applied.get("tun", {})
                    require(code == 200 and tun.get("enable") is True and tun.get("device") == name,
                            "immediate same-name launch did not apply its TUN")
                    require("Tun adapter listening" in recovered.log_tail(),
                            "immediate same-name launch did not create a real TUN")
                    report["tun_immediate_recreate_seconds"] = round(time.monotonic() - recovered.started, 3)
                    recovered.shutdown()
        report["checks"].append("real_tun_cleanup_then_immediate_same_name_recreate")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core", required=True, type=Path)
    parser.add_argument("--require-tun", action="store_true")
    parser.add_argument("--timeout", type=float, default=15)
    parser.add_argument("--report", type=Path)
    arguments = parser.parse_args()
    report: dict = {"passed": False, "checks": [], "stage": "setup"}
    result = 1
    try:
        executable = arguments.core.resolve(strict=True)
        require(executable.is_file(), "--core must name a compiled executable")
        require(0 < arguments.timeout <= 120, "--timeout must be between 0 and 120 seconds")
        require(not arguments.require_tun or os.name == "nt", "--require-tun requires Windows")
        with executable.open("rb") as source:
            report["core_sha256"] = hashlib.file_digest(source, "sha256").hexdigest()
        with tempfile.TemporaryDirectory(prefix="mihomo-controller-") as directory:
            run(executable, Path(directory).resolve(), arguments.timeout, report, arguments.require_tun)
        report["passed"], report["stage"], result = True, "complete", 0
    except (Failure, OSError, ValueError, http.client.HTTPException, subprocess.SubprocessError) as error:
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
