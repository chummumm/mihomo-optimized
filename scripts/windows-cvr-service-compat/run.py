#!/usr/bin/env python3
"""Exercise the official production CVR Service on a disposable GitHub runner.

No GUI, Rust toolchain, test-feature service, or third-party Python package is
needed. The small IPC client is pinned to the public v2.7.7 protocol; transport
and actual TUN traffic use ../test-windows-tun-compat.py.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import traceback
import urllib.request
import zipfile


HERE = Path(__file__).resolve().parent
SERVICE_COMMIT = "3ed29700b093190b5e7addcaa892e77f172af544"
SERVICE_VERSION = "2.7.7"
SERVICE_NAME = "clash_verge_service"
SERVICE_PIPE = r"\\.\pipe\clash-verge-service"
SERVICE_PROTOCOL = "2.6"
IPC_MAGIC = ("A thing of beauty is a joy for ever. Its loveliness increases; "
             "it will never pass into nothingness.")
ASSET_NAME = "clash-verge-service-ipc-v2.7.7-x86_64-pc-windows-msvc.zip"
ASSET_URL = "https://github.com/clash-verge-rev/clash-verge-service-ipc/releases/download/v2.7.7/" + ASSET_NAME
ASSET_SHA256 = "91316df6662b0962d13dcf18f71d0182a62dc5a1a68769a3adc34cda07538bdf"
BUNDLE_FILES = {"clash-verge-service.exe", "clash-verge-service-install.exe",
                "clash-verge-service-uninstall.exe", "LICENSE"}

spec = importlib.util.spec_from_file_location("windows_tun_compat", HERE.parent / "test-windows-tun-compat.py")
compat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compat)
require = compat.require


def core_pipe(credentials: dict) -> str:
    key = hashlib.sha256(credentials["identity"]["Windows"]["sid"].encode()).hexdigest()
    return rf"\\.\pipe\verge-mihomo-production-{key}"


def start_payload(core: Path, config: dict, token: str) -> dict:
    # JSON is valid YAML and avoids a dependency just to serialize this fixture.
    return {"runtime": {"yaml": json.dumps(config), "assets": [], "remote_providers": [],
                        "core_path": str(core)},
            "proposed_session_token": token, "macos_proxy": None}


def require_empty_installation(snapshot: dict) -> None:
    require(snapshot["service"] is None, "refusing to replace an existing Clash Verge service")
    require(not snapshot["rootExists"], "refusing to reuse an existing Clash Verge service state directory")


def same_path(actual: str, expected: Path) -> bool:
    return os.path.normcase(os.path.abspath(actual)) == os.path.normcase(str(expected))


def require_service_identity(snapshot: dict, executable: Path, *, running: bool) -> dict:
    service = snapshot["service"]
    require(isinstance(service, dict) and service["name"] == SERVICE_NAME,
            "the test's registered service is unavailable")
    # SCM registration must contain only this installed executable, no arguments.
    registered_path = service["path"]
    if registered_path.startswith('"') and registered_path.endswith('"'):
        registered_path = registered_path[1:-1]
    require('"' not in registered_path and same_path(registered_path, executable)
            and service["account"].lower() in {"localsystem", r"nt authority\system"},
            "refusing to use or uninstall a service with a different executable/account")
    if running:
        require(service["state"] == "Running" and service["pid"] > 0,
                "the installed official service is not running")
    return service


def extract_bundle(data: bytes, destination: Path, *, expected_hash: str = ASSET_SHA256) -> None:
    require(hashlib.sha256(data).hexdigest() == expected_hash, "official service release SHA-256 mismatch")
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        require(len(entries) == len(BUNDLE_FILES) and {entry.filename for entry in entries} == BUNDLE_FILES,
                "official service archive contains unexpected or duplicate paths")
        for entry in entries:
            require(not entry.is_dir() and not stat.S_ISLNK(entry.external_attr >> 16)
                    and 0 < entry.file_size <= 16 * 1024 * 1024, "invalid official service archive entry")
        for entry in entries:
            (destination / entry.filename).write_bytes(archive.read(entry))


def command_line_arguments(value: str) -> list[str]:
    shell = ctypes.WinDLL("shell32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    shell.CommandLineToArgvW.argtypes = [ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_int)]
    shell.CommandLineToArgvW.restype = ctypes.POINTER(ctypes.c_wchar_p)
    kernel.LocalFree.argtypes, kernel.LocalFree.restype = [ctypes.c_void_p], ctypes.c_void_p
    count = ctypes.c_int()
    arguments = shell.CommandLineToArgvW(value, ctypes.byref(count))
    require(arguments, "cannot parse the service-spawned core's Windows command line")
    try:
        return [arguments[index] for index in range(count.value)]
    finally:
        kernel.LocalFree(arguments)


class Acceptance:
    def __init__(self, core: Path, report: Path, work: Path) -> None:
        self.core, self.report_path, self.work = core, report, work
        self.nonce = secrets.token_hex(8)
        self.evidence = report.parent / (report.stem + "-evidence-" + self.nonce)
        self.evidence.mkdir(parents=True)
        self.report = {"startedAt": compat.timestamp(), "status": "running",
                       "officialService": {"version": SERVICE_VERSION, "commit": SERVICE_COMMIT,
                                           "asset": ASSET_URL, "sha256": ASSET_SHA256,
                                           "productionBinary": True, "protocol": SERVICE_PROTOCOL},
                       "cycles": [], "cleanup": {"attempted": False}}
        self.windows = None
        self.state_root = None
        self.service_pid = None
        self.service_held = None
        self.service_image = None
        self.installed_core = None
        self.installer_invoked = False
        self.credentials = None
        self.session = None
        self.socks = None
        self.held = []
        self.tokens = []
        self.name = "MihomoCompat-" + self.nonce
        self.pwsh = shutil.which("pwsh")
        self.checkpoint()

    def redact(self, value: str) -> str:
        for token in self.tokens:
            value = value.replace(token, "[test credential redacted]")
        return value

    def checkpoint(self) -> None:
        temporary = self.report_path.with_suffix(".tmp")
        temporary.write_text(self.redact(json.dumps(self.report, indent=2)), encoding="utf-8")
        temporary.replace(self.report_path)

    def powershell(self, action: str, **arguments) -> dict:
        require(self.pwsh, "the GitHub Windows runner must provide PowerShell 7")
        command = [self.pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                   str(HERE / "windows.ps1"), "-Action", action]
        for key, value in arguments.items():
            command.extend(["-" + key, str(value)])
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace",
                                env=compat.child_environment(), timeout=30,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        require(result.returncode == 0, "Windows acceptance helper failed: " + self.redact(result.stderr[-4096:]))
        return json.loads(result.stdout.lstrip("\ufeff"))

    def snapshot(self, core_pid: int = 0) -> dict:
        return self.powershell("Snapshot", CoreProcessId=core_pid)

    def tool(self, binary: str, arguments: list[str], timeout: float) -> None:
        result = subprocess.run([str(self.work / binary), *arguments], capture_output=True, text=True,
                                encoding="utf-8", errors="replace", env=compat.child_environment(),
                                timeout=timeout, creationflags=subprocess.CREATE_NO_WINDOW)
        (self.evidence / (binary + ".log")).write_text(
            self.redact(result.stdout + result.stderr), encoding="utf-8")
        require(result.returncode == 0, f"official {binary} failed with exit code {result.returncode}")

    def service_call(self, method: str, path: str, payload=None, *, session=None) -> dict:
        require(self.service_held and not self.windows.process_state(self.service_held)["exited"],
                "the pinned official service process exited before the request")
        envelope = {"credentials": self.credentials, "payload": payload}
        if session is not None:
            envelope["session"] = session
        response = compat.pipe_request(SERVICE_PIPE, method, path, envelope, timeout=35,
            headers={"X-Clash-Verge-Service-Protocol": SERVICE_PROTOCOL}, expected_pid=self.service_pid)
        require(response["status"] == 200 and isinstance(response["body"], dict)
                and response["body"].get("code") == 0,
                f"official service {method} {path} failed: {self.redact(str(response))}")
        return response["body"].get("data")

    def prepare(self) -> None:
        require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true"
                and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted",
                "service installation is restricted to disposable GitHub-hosted Windows runners")
        self.windows = compat.Windows()
        before = self.snapshot()
        require_empty_installation(before)
        self.state_root = Path(before["root"])
        self.service_image = self.state_root / "bin" / "clash-verge-service.exe"
        require(not self.windows.interfaces(self.name), "isolated test adapter alias/GUID already exists")
        self.report["preflight"] = before
        require(self.core.is_file(), "compiled core is missing")
        version = subprocess.run([str(self.core), "-v"], capture_output=True, text=True, encoding="utf-8",
                                 errors="replace", env=compat.child_environment(), timeout=15,
                                 creationflags=subprocess.CREATE_NO_WINDOW)
        match = re.search(r"^(\S+) windows amd64 with ", version.stdout, flags=re.MULTILINE)
        require(version.returncode == 0 and match, "compiled core did not report a Windows amd64 version")
        self.core_version = match.group(1)
        self.core_digest = compat.file_hash(self.core)
        self.report["core"] = {"source": str(self.core), "sha256": self.core_digest,
                               "version": self.core_version, "versionOutput": version.stdout}
        self.checkpoint()

        with urllib.request.urlopen(ASSET_URL, timeout=30) as response:
            archive = response.read(16 * 1024 * 1024 + 1)
        require(len(archive) <= 16 * 1024 * 1024, "official service archive exceeds limit")
        extract_bundle(archive, self.work)
        self.service_digest = compat.file_hash(self.work / "clash-verge-service.exe")
        self.report["officialService"]["executableSha256"] = self.service_digest
        source = self.work / ("mihomo-cvr-compat-" + self.nonce + ".exe")
        shutil.copyfile(self.core, source)
        require(compat.file_hash(source) == self.core_digest, "test core copy changed")
        self.requested_core = source
        self.installed_core = self.state_root / "cores" / source.name
        self.credentials = self.powershell("Credentials", OwnerRoot=self.work / "owner")
        self.tokens.append(self.credentials["token"])
        self.pipe = core_pipe(self.credentials)
        self.socks = compat.SocksFixture()
        self.config = compat.configuration(self.name, self.socks.server_address[1])
        (self.evidence / "core-config.json").write_text(json.dumps(self.config, indent=2), encoding="utf-8")

        # Check again immediately before the only installer invocation. No
        # existing service or state is ever accepted as an upgrade target.
        require_empty_installation(self.snapshot())
        self.installer_invoked = True
        self.tool("clash-verge-service-install.exe",
                  ["--install-service", "--install-core", str(source), "--sha256", self.core_digest], 90)
        installed = self.snapshot()
        registered = require_service_identity(installed, self.service_image, running=True)
        require(compat.file_hash(self.service_image) == self.service_digest
                and compat.file_hash(self.installed_core) == self.core_digest,
                "the official installer published different binaries")
        self.service_pid = registered["pid"]
        self.service_held = self.windows.hold(self.service_pid, self.service_image)
        self.report["installation"] = installed
        self.report["core"]["installed"] = str(self.installed_core)
        response = compat.pipe_request(SERVICE_PIPE, "GET", "/version", headers={"X-IPC-Magic": IPC_MAGIC},
                                       expected_pid=self.service_pid)
        require(response["status"] == 200 and response["body"].get("code") == 0,
                "official service version request failed")
        info = response["body"]["data"]
        require(info["build_version"] == SERVICE_VERSION and info["protocol"] == {"epoch": 2, "revision": 6},
                "the installed service is not the pinned production protocol")
        self.report["officialService"]["versionResponse"] = info
        # The runner's elevated installer token can create a file owned by the
        # runner user. Meet the official service's owner requirement on only
        # this fresh approved core, preserving its DACL and attested bytes.
        self.report["core"]["installationSecurity"] = self.powershell(
            "NormalizeCoreOwner", CorePath=self.installed_core, CoreSHA256=self.core_digest)
        self.checkpoint()

    def cycle(self, number: int, *, release_resources: bool = False) -> None:
        if release_resources:
            record = {"purpose": "harness_resource_release", "startedAt": compat.timestamp()}
            self.report["harness_resource_release"] = record
            require(compat.file_hash(self.installed_core) == self.core_digest,
                    "approved core changed before test resource release")
        else:
            record = {"cycle": number, "startedAt": compat.timestamp()}
            self.report["cycles"].append(record)
        token = secrets.token_hex(32)
        self.tokens.append(token)
        result = self.service_call("POST", "/clash/start", start_payload(self.requested_core, self.config, token))
        self.session = {"generation": result["session"]["generation"], "token": token}
        status = self.service_call("GET", "/status")
        record["startedStatus"] = status
        require(status["is_active"] is True and status["desired_core_should_be_running"] is True
                and status["active_generation"] == self.session["generation"] and status["core_pid"] > 0,
                "service did not commit the requested active core session")
        held = self.windows.hold(status["core_pid"], self.installed_core)
        self.held.append(held)
        process = self.snapshot(held["pid"])["coreProcess"]
        require(process and process["parentPid"] == self.service_pid
                and same_path(process["path"], self.installed_core), "core was not spawned by the official service")
        arguments = command_line_arguments(process["commandLine"])
        require(len(arguments) == 7 and arguments[1::2] == ["-d", "-f", "-ext-ctl-pipe"]
                and arguments[6] == self.pipe and "-desktop-managed" not in arguments,
                "official service did not use its ordinary core command line")
        record["process"] = process
        record["arguments"] = arguments
        record["sessionGeneration"] = self.session["generation"]
        for path in ("/version", "/configs", "/desktop"):
            response = compat.pipe_request(self.pipe, "GET", path, expected_pid=held["pid"])
            if path == "/version":
                require(response["status"] == 200 and response["body"].get("version") == self.core_version,
                        "service-spawned core version differs from the compiled executable")
                record["coreVersion"] = response["body"]
            elif path == "/configs":
                tun = response["body"].get("tun", {})
                require(response["status"] == 200 and tun.get("enable") is True
                        and tun.get("device") == self.name and tun.get("auto-route") is True,
                        "service-spawned core did not apply the TUN configuration")
                record["tunConfig"] = tun
            else:
                require(response["status"] == 404, "official service unexpectedly enabled desktop-managed mode")
        record["identity"] = self.windows.generic(self.name)
        if release_resources:
            require(not self.windows.process_state(held)["exited"],
                    "resource-release core exited before TUN shutdown")
            release = {"method": "PATCH /configs tun.enable=false", "expectedPid": held["pid"],
                       "requestedAt": compat.timestamp()}
            record["tunRelease"] = release
            self.checkpoint()
            response = compat.pipe_request(self.pipe, "PATCH", "/configs", {"tun": {"enable": False}},
                                           expected_pid=held["pid"])
            require(response["status"] == 204, "test resource release could not disable TUN")
            response = compat.pipe_request(self.pipe, "GET", "/configs", expected_pid=held["pid"])
            tun = response["body"].get("tun", {})
            require(response["status"] == 200 and tun.get("enable") is False,
                    "test resource release did not confirm disabled TUN")
            release["confirmedTunConfig"] = tun
            deadline = time.monotonic() + 10
            rows = self.windows.interfaces(self.name)
            while rows and time.monotonic() < deadline:
                time.sleep(0.1)
                rows = self.windows.interfaces(self.name)
            release["remainingInterfaces"] = rows
            self.checkpoint()
            require(not rows, "test TUN remained after the explicit resource-release request")
            require(not self.windows.process_state(held)["exited"],
                    "resource-release core exited before TUN shutdown was confirmed")
            release["confirmedAt"] = compat.timestamp()
        else:
            record["forwarding"] = compat.verify_tun_forwarding(
                self.socks, alive=lambda: not self.windows.process_state(held)["exited"])
        log = self.service_call("GET", "/clash/log-snapshot")
        log_name = "core-resource-release.log" if release_resources else f"core-cycle-{number}.log"
        (self.evidence / log_name).write_text(self.redact(log), encoding="utf-8")
        self.checkpoint()

        proof = self.session
        # A timed-out mutation is not retried. The official uninstaller remains
        # the bounded final cleanup path if this call's outcome is unknown.
        self.session = None
        self.service_call("DELETE", "/clash/stop", session=proof)
        after = self.service_call("GET", "/status")
        require(after["is_active"] is False and after["core_pid"] is None
                and after["desired_core_should_be_running"] is False,
                "official service stop did not clear the desired/running core state")
        require(self.windows.api.WaitForSingleObject(held["handle"], 10000) == compat.WAIT_OBJECT_0,
                "service stop acknowledged before its core exited")
        record["stoppedStatus"] = after
        record["stoppedProcess"] = self.windows.process_state(held)
        record["stopMechanism"] = "official DELETE /clash/stop; Windows service uses Child.kill/TerminateProcess"
        record["finishedAt"] = compat.timestamp()
        self.checkpoint()
        # Do not wait for interface disappearance here. The next real service
        # start must handle the native teardown state itself.

    def diagnostics(self) -> None:
        if not self.installer_invoked:
            return
        try:
            self.report["finalSnapshot"] = self.snapshot()
            if self.service_pid and self.credentials and self.report["status"] != "passed":
                data = self.service_call("GET", "/clash/log-snapshot")
                if isinstance(data, str):
                    (self.evidence / "core-final.log").write_text(self.redact(data), encoding="utf-8")
        except Exception as error:
            self.report["diagnosticError"] = self.redact(str(error))
        if self.state_root:
            logs = self.state_root / "logs"
            if logs.is_dir():
                for index, path in enumerate(sorted(logs.iterdir())):
                    if index >= 8:
                        break
                    if path.is_file() and not path.is_symlink():
                        with path.open("rb") as stream:
                            stream.seek(max(0, path.stat().st_size - 512 * 1024))
                            content = stream.read().decode("utf-8", errors="replace")
                        (self.evidence / f"service-{index}.log").write_text(self.redact(content), encoding="utf-8")

    def cleanup(self) -> None:
        errors = []
        self.report["cleanup"]["attempted"] = self.installer_invoked
        try:
            if self.installer_invoked:
                observed = self.snapshot()
                if observed["service"] is not None:
                    require_service_identity(observed, self.service_image, running=False)
                # The state root did not exist before this exact installer
                # call. Refuse uninstall if its binaries/cores have changed.
                if self.service_image.exists():
                    require(compat.file_hash(self.service_image) == self.service_digest,
                            "service image changed; refusing to uninstall an unknown installation")
                cores = self.state_root / "cores"
                if cores.exists():
                    require({path.name for path in cores.iterdir()} <=
                            {self.installed_core.name, self.installed_core.name + ".next"},
                            "unrecognized approved core appeared; refusing broad official uninstall")
                if self.installed_core.exists():
                    require(compat.file_hash(self.installed_core) == self.core_digest,
                            "approved core changed; refusing uninstall")
                if self.session and observed["service"] is not None:
                    try:
                        self.service_call("DELETE", "/clash/stop", session=self.session)
                        self.session = None
                    except Exception as error:
                        errors.append("service session stop: " + self.redact(str(error)))
                self.tool("clash-verge-service-uninstall.exe", [], 70)
                after = self.snapshot()
                require(after["service"] is None and not self.service_image.exists()
                        and not self.installed_core.exists(), "official service/core uninstall was incomplete")
                self.report["cleanup"].update({"serviceRemoved": True, "approvedCoreRemoved": True,
                                               "officialUninstaller": True})
                if self.service_held:
                    require(self.windows.api.WaitForSingleObject(self.service_held["handle"], 10000)
                            == compat.WAIT_OBJECT_0, "official service process survived uninstall")
            if self.windows:
                for held in self.held:
                    require(self.windows.api.WaitForSingleObject(held["handle"], 10000) == compat.WAIT_OBJECT_0,
                            "owned core survived service cleanup")
                deadline = time.monotonic() + 10
                rows = self.windows.interfaces(self.name)
                while rows and time.monotonic() < deadline:
                    time.sleep(0.1)
                    rows = self.windows.interfaces(self.name)
                self.report["cleanup"]["remainingInterfaces"] = rows
                require(not rows, "test TUN remained after official service cleanup")
        except Exception as error:
            errors.append(self.redact(str(error)))
        finally:
            if self.socks:
                try:
                    self.socks.close()
                except Exception as error:
                    errors.append("SOCKS fixture cleanup: " + str(error))
            if self.windows:
                for held in self.held:
                    self.windows.api.CloseHandle(held["handle"])
                if self.service_held:
                    self.windows.api.CloseHandle(self.service_held["handle"])
            self.report["cleanup"]["errors"] = errors
            self.checkpoint()
        require(not errors, "official service cleanup failed: " + "; ".join(errors))

    def run(self) -> None:
        try:
            self.prepare()
            self.cycle(1)
            self.cycle(2)
            # Both compatibility cycles must finish before any deliberate TUN
            # shutdown. Preserve what the second official forced stop left.
            self.report["cycles"][-1]["interfacesAfterStop"] = self.windows.interfaces(self.name)
            self.checkpoint()
            self.cycle(3, release_resources=True)
            self.report["status"] = "passed"
        except Exception as error:
            self.report.update({"status": "failed", "error": self.redact(str(error))})
            raise
        finally:
            try:
                try:
                    self.diagnostics()
                except Exception as error:
                    self.report["diagnosticError"] = self.redact(str(error))
            finally:
                try:
                    self.cleanup()
                except Exception:
                    self.report["status"] = "failed"
                    raise
                finally:
                    self.report["finishedAt"] = compat.timestamp()
                    self.checkpoint()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    arguments = parser.parse_args()
    report = arguments.report.resolve()
    report.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="mihomo-cvr-service-compat-") as directory:
        acceptance = Acceptance(arguments.core.resolve(), report, Path(directory))
        try:
            acceptance.run()
        except Exception:
            traceback.print_exc()
            return 1
    print(f"Official CVR Service {SERVICE_VERSION} acceptance passed; evidence: {report}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
