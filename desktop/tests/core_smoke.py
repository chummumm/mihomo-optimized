#!/usr/bin/env python3
"""Exercise a built desktop-managed core with Python's standard library only.

Run on the target platform: python desktop/tests/core_smoke.py --core <exe>
All traffic stays on loopback. This checks the real process, HTTP forwarding,
reload, local rule updates and shutdown; it does not test TUN or Windows UAC.
"""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import http.client
import http.server
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time


class SmokeFailure(Exception):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SmokeFailure(message)


def available_ports(count: int) -> list[int]:
    # Hold every reservation until all ports are distinct. The supervisor's
    # authenticated PID check below detects a different process winning a bind.
    with contextlib.ExitStack() as stack:
        listeners = [stack.enter_context(socket.socket()) for _ in range(count)]
        for listener in listeners:
            listener.bind(("127.0.0.1", 0))
        return [listener.getsockname()[1] for listener in listeners]


def is_listening(port: int) -> bool:
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=0.3):
            return True
    except OSError:
        return False


def binary_digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def child_environment(secret: str) -> dict[str, str]:
    # Do not forward user profiles, proxy settings, CLASH overrides or unrelated
    # credentials from the runner. Keep only OS process/runtime requirements.
    allowed = {"SYSTEMROOT", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP",
               "TMPDIR", "SYSTEMDRIVE", "COMSPEC", "TZ"}
    result = {key: value for key, value in os.environ.items() if key.upper() in allowed}
    result["CLASH_OVERRIDE_SECRET"] = secret
    # These legacy escapes must not weaken managed mode.
    result["SKIP_SAFE_PATH_CHECK"] = "true"
    return result


class LocalOrigin(http.server.ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self) -> None:
        super().__init__(("127.0.0.1", 0), OriginHandler)
        self.revision = 1
        self.rule_requests = 0
        self.proxy_requests = 0
        self.unexpected_requests = 0
        self.lock = threading.Lock()

    def rule_document(self) -> bytes:
        return f"payload:\n  - '+.revision-{self.revision}.invalid'\n".encode()


class OriginHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self) -> None:
        origin = self.server
        with origin.lock:
            if self.path == "/rules.yaml":
                origin.rule_requests += 1
                body = origin.rule_document()
                status = 200
            elif self.path.startswith("/roundtrip?"):
                origin.proxy_requests += 1
                body = b"mihomo-desktop-loopback-roundtrip\n"
                status = 200
            else:
                origin.unexpected_requests += 1
                body, status = b"unexpected request\n", 404
        self.send_response(status)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Content-Type", "text/plain")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format: str, *args: object) -> None:
        pass


def configuration(mixed: int, forbidden: int, origin: int, home: Path,
                  mode: str = "rule") -> str:
    return f"""mixed-port: {mixed}
port: 0
socks-port: 0
allow-lan: false
bind-address: 127.0.0.1
mode: {mode}
log-level: warning
ipv6: false
find-process-mode: 'off'
dns:
  enable: false
tun:
  enable: false
profile:
  store-selected: false
  store-fake-ip: false
geo-auto-update: false
core-auto-update: true
external-ui-auto-update: true
external-ui: ui
external-ui-url: http://127.0.0.1:{origin}/ui-must-not-download.zip
external-controller: 0.0.0.0:{forbidden}
external-controller-tls: 127.0.0.1:{forbidden}
external-controller-unix: {json.dumps(str(home / 'forbidden.sock'))}
external-controller-pipe: must-not-open-desktop-smoke
secret: yaml-secret-must-never-authenticate
rule-providers:
  smoke:
    type: http
    behavior: domain
    format: yaml
    path: ./rules/smoke.yaml
    url: http://127.0.0.1:{origin}/rules.yaml
    interval: 0
    proxy: DIRECT
rules:
  - RULE-SET,smoke,DIRECT
  - MATCH,DIRECT
"""


class ManagedCore:
    def __init__(self, executable: Path, home: Path, port: int, timeout: float) -> None:
        self.home, self.port, self.timeout = home, port, timeout
        self.secret = secrets.token_urlsafe(32)
        self.log = tempfile.TemporaryFile()
        self.process = subprocess.Popen(
            [str(executable), "-desktop-managed", "-d", str(home),
             "-f", str(home / "config.yaml"), "-ext-ctl", f"127.0.0.1:{port}"],
            cwd=home, env=child_environment(self.secret), stdin=subprocess.DEVNULL,
            stdout=self.log, stderr=subprocess.STDOUT,
        )

    def request(self, method: str, path: str, expected: int,
                body: object = None, token: str | None = "managed") -> dict:
        headers = {"Connection": "close"}
        if token is not None:
            headers["Authorization"] = "Bearer " + (self.secret if token == "managed" else token)
        payload = None if body is None else json.dumps(body).encode()
        if payload is not None:
            headers["Content-Type"] = "application/json"
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=2)
        try:
            connection.request(method, path, body=payload, headers=headers)
            response = connection.getresponse()
            data = response.read()
            require(response.status == expected,
                    f"{method} {path}: expected HTTP {expected}, got {response.status}")
            require(self.secret.encode() not in data, f"{path} exposed its runtime secret")
            return json.loads(data) if data else {}
        finally:
            connection.close()

    def wait_ready(self) -> dict:
        deadline = time.monotonic() + self.timeout
        while time.monotonic() < deadline:
            require(self.process.poll() is None, "core exited before managed readiness")
            try:
                status = self.request("GET", "/desktop", 200)
            except (OSError, http.client.HTTPException):
                time.sleep(0.05)
                continue
            require(status.get("managed") is True and status.get("api_version") == 1,
                    "core does not implement desktop-managed API 1")
            require(status.get("pid") == self.process.pid,
                    "controller belongs to a different process")
            if status.get("state") == "running":
                return status
            time.sleep(0.05)
        raise SmokeFailure("core did not reach managed running state")

    def close(self) -> None:
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=3)
        self.log.close()


def proxy_roundtrip(port: int, origin: LocalOrigin, phase: str) -> None:
    target = f"127.0.0.1:{origin.server_port}"
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    try:
        # Absolute-form HTTP explicitly traverses mixed-port. No urllib/system
        # proxy bypass or DNS resolution is involved.
        connection.request("GET", f"http://{target}/roundtrip?phase={phase}",
                           headers={"Host": target, "Connection": "close"})
        response = connection.getresponse()
        body = response.read()
        require(response.status == 200 and body == b"mihomo-desktop-loopback-roundtrip\n",
                f"mixed-port HTTP forwarding failed during {phase}")
    finally:
        connection.close()


def assert_program_updates_skipped(snapshot: dict) -> None:
    items = {item["id"]: item for item in snapshot.get("items", [])}
    for name in ("kernel", "ui"):
        item = items.get(name, {})
        require(item.get("status") == "skipped" and item.get("reason") == "desktop_managed"
                and item.get("auto_update") is False,
                f"{name} update escaped desktop ownership")


def rejected_startup(executable: Path, root: Path, case: str, timeout: float) -> None:
    home = root / case
    home.mkdir()
    config = home / "config.yaml"
    config.write_text("mode: direct\nmixed-port: 0\nrules: ['MATCH,DIRECT']\n", encoding="utf-8")
    secret = secrets.token_urlsafe(32)
    port = available_ports(1)[0]
    address = f"127.0.0.1:{port}"
    if case == "invalid-yaml":
        config.write_text("mixed-port: [unterminated\n", encoding="utf-8")
    elif case == "invalid-secret":
        secret = "too-short"
    elif case == "unsafe-controller":
        address = f"0.0.0.0:{port}"
    elif case == "outside-config":
        config = root / "outside.yaml"
        config.write_text("mode: direct\n", encoding="utf-8")
    with tempfile.TemporaryFile() as log:
        process = subprocess.Popen(
            [str(executable), "-desktop-managed", "-d", str(home), "-f", str(config),
             "-ext-ctl", address], cwd=home, env=child_environment(secret),
            stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
        )
        try:
            try:
                result = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired as error:
                raise SmokeFailure(f"{case}: unsafe startup did not fail promptly") from error
            require(result != 0, f"{case}: unsafe startup was accepted")
            require(not is_listening(port), f"{case}: controller survived a rejected startup")
            log.seek(0)
            require(secret.encode() not in log.read(), f"{case}: core logged a bootstrap secret")
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=3)


def run(executable: Path, timeout: float) -> None:
    original_digest = binary_digest(executable)
    with tempfile.TemporaryDirectory(prefix="mihomo-desktop-smoke-") as temporary:
        root = Path(temporary).resolve()
        home = root / "managed"
        (home / "rules").mkdir(parents=True)
        origin = LocalOrigin()
        origin_thread = threading.Thread(target=origin.serve_forever, daemon=True)
        origin_thread.start()
        core = None
        try:
            controller, mixed, forbidden = available_ports(3)
            config_path = home / "config.yaml"
            config_path.write_text(configuration(mixed, forbidden, origin.server_port, home), encoding="utf-8")
            provider_path = home / "rules" / "smoke.yaml"
            provider_path.write_bytes(origin.rule_document())
            core = ManagedCore(executable, home, controller, timeout)
            status = core.wait_ready()
            require(status.get("update_owner") == "desktop", "wrong update owner")
            caps = status.get("capabilities", {})
            require(caps.get("unified_update_scope") == "rules_and_geodata"
                    and caps.get("shutdown") is True and caps.get("config_reload") is True,
                    "required desktop capabilities missing")
            for token in (None, "wrong-smoke-secret", "yaml-secret-must-never-authenticate"):
                core.request("GET", "/desktop", 401, token=token)
            core.request("POST", "/desktop/shutdown", 401, token=None)
            core.request("POST", "/desktop/shutdown", 400, body={})
            for endpoint in ("/restart", "/upgrade", "/upgrade/ui"):
                result = core.request("POST", endpoint, 409)
                require(result.get("code") == "desktop_managed", f"{endpoint}: wrong refusal contract")
            require(not is_listening(forbidden), "YAML opened an additional controller")
            print("PASS: managed API identity, authentication and process ownership", flush=True)

            proxy_roundtrip(mixed, origin, "startup")
            config_path.write_text(configuration(mixed, forbidden, origin.server_port, home, "direct"), encoding="utf-8")
            core.request("PUT", "/configs?force=true", 204, body={"path": str(config_path)})
            require(core.request("GET", "/configs", 200).get("mode") == "direct",
                    "force reload did not apply the file")
            reloaded = core.wait_ready()
            require(reloaded.get("controller") == f"127.0.0.1:{controller}",
                    "force reload changed the managed controller")
            core.request("GET", "/desktop", 401, token=None)
            core.request("GET", "/desktop", 401, token="yaml-secret-must-never-authenticate")
            require(not is_listening(forbidden), "force reload exposed an extra controller")
            proxy_roundtrip(mixed, origin, "force-reload")
            require(origin.proxy_requests == 2, "origin did not receive both forwarded requests")
            print("PASS: local HTTP roundtrip through mixed-port before and after force reload", flush=True)

            assert_program_updates_skipped(core.request("GET", "/upgrade/check", 200))
            with origin.lock:
                origin.revision = 2
                requests_before = origin.rule_requests
            started = core.request("POST", "/upgrade/check", 202)
            deadline = time.monotonic() + timeout
            while True:
                update = core.request("GET", "/upgrade/check", 200)
                require(update.get("task_id") == started.get("task_id"), "update task identity changed")
                assert_program_updates_skipped(update)
                if update.get("state") not in ("running", "idle"):
                    break
                require(time.monotonic() < deadline, "data update did not finish")
                time.sleep(0.05)
            require(update.get("state") == "completed", "local data update failed or requested restart")
            items = {item["id"]: item for item in update.get("items", [])}
            require(items.get("rule-provider:smoke", {}).get("status") == "updated",
                    "managed update did not refresh the configured HTTP rule provider")
            require(origin.rule_requests > requests_before and b"revision-2" in provider_path.read_bytes(),
                    "rule update did not commit the loopback origin's new payload")
            require(not (home / "ui").exists() and origin.unexpected_requests == 0,
                    "managed core attempted a UI download")
            require(binary_digest(executable) == original_digest, "managed core replaced its executable")
            require(core.process.poll() is None and core.request("GET", "/desktop", 200)["pid"] == status["pid"],
                    "data update restarted the core")
            require(core.secret not in config_path.read_text(encoding="utf-8"),
                    "runtime secret was persisted to YAML")
            print("PASS: local rule update completed; binary and UI updates stayed skipped", flush=True)

            require(core.request("POST", "/desktop/shutdown", 202).get("status") == "stopping",
                    "shutdown acknowledgement was incomplete")
            try:
                result = core.process.wait(timeout=timeout)
            except subprocess.TimeoutExpired as error:
                raise SmokeFailure("acknowledged shutdown did not exit the process") from error
            require(result == 0, "graceful shutdown exited with failure")
            require(not is_listening(controller) and not is_listening(mixed),
                    "core ports remained open after process exit")
            core.log.seek(0)
            require(core.secret.encode() not in core.log.read(), "core logged its runtime secret")
            print("PASS: complete shutdown acknowledgement, process exit and closed ports", flush=True)
        finally:
            if core is not None:
                core.close()
            origin.shutdown()
            origin.server_close()
            origin_thread.join(timeout=3)

        for case in ("invalid-yaml", "invalid-secret", "unsafe-controller", "outside-config"):
            rejected_startup(executable, root, case, timeout)
        print("PASS: invalid YAML, weak secret, unsafe controller and outside-home config rejected", flush=True)
    require(binary_digest(executable) == original_digest, "smoke changed the supplied core executable")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core", required=True, type=Path, help="fixed built core executable to exercise")
    parser.add_argument("--timeout", default=30.0, type=float, help="seconds per bounded startup/update/shutdown phase")
    args = parser.parse_args()
    try:
        executable = args.core.resolve(strict=True)
        require(executable.is_file(), "--core must point to an existing executable file")
        require(args.timeout > 0, "--timeout must be positive")
        run(executable, args.timeout)
    except (SmokeFailure, OSError, ValueError, http.client.HTTPException) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print("PASS: desktop-managed core black-box smoke (no TUN or external network)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
