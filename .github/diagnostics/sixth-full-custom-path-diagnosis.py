#!/usr/bin/env python3
"""Exact sixth candidate, original path suite, bounded failure observations."""

from contextlib import contextmanager
import ctypes
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import threading
import time
import traceback

ROOT = Path(os.environ["GITHUB_WORKSPACE"])
sys.path.insert(0, str(ROOT / "desktop" / "tests"))

import custom_install_smoke as custom
from custom_install_smoke import CustomInstallAcceptance, REQUIRED_CASES
from upgrade_smoke import digest, failure_code
from upgrade_tun_native import NativeTunIdentity
from upgrade_tun_smoke import TunUpgradeAcceptance
from windows_smoke import require

SOURCE = "84cc0f8baadd84cb768b3907a1c7a61aa10af3fd"
INSTALLER_SHA256 = "e05ea9dd5328190e3a4be1dd3cc63377f1e6ec03ea6b361cc3e39a99e193b461"
SKIPPED_CASE = "custom_core_update"
SKIP_REASON = "The source-built core-update baseline was not uploaded with this candidate artifact."
SAFE_RUNTIME_CODES = frozenset({"no_available_tcp_udp_fixture_port", "upgrade_device_enumeration_limit",
                                "upgrade_tcp_owner_table_kept_growing"})
PORT_OBSERVATION = {"activeThread": None, "count": 0, "firstPorts": [], "lastPort": None}


def observe_bind(event, arguments) -> None:
    # An observation hook only; it never binds a socket, retries, or raises.
    if event != "socket.bind" or PORT_OBSERVATION["activeThread"] != threading.get_ident():
        return
    try:
        listener, address = arguments
        if (listener.family == socket.AF_INET and listener.type & 0xF == socket.SOCK_STREAM
                and isinstance(address, tuple) and len(address) == 2 and address[0] == "127.0.0.1"
                and type(address[1]) is int and 0 < address[1] <= 65535):
            PORT_OBSERVATION["count"] += 1
            PORT_OBSERVATION["lastPort"] = address[1]
            if len(PORT_OBSERVATION["firstPorts"]) < 16:
                PORT_OBSERVATION["firstPorts"].append(address[1])
    except Exception:
        pass


def exception_observation(error: Exception) -> dict:
    # No exception text, source lines, frame locals, controller values or tokens.
    result = {"type": type(error).__name__, "frames": [
        {"file": Path(frame.filename).name, "line": frame.lineno, "function": frame.name}
        for frame in traceback.extract_tb(error.__traceback__)[-12:]]}
    if type(error) is RuntimeError and str(error) in SAFE_RUNTIME_CODES:
        result["fixedRuntimeCode"] = str(error)
    return result


class DiagnosticAcceptance(CustomInstallAcceptance):
    def observation(self, label: str, stage: str, state: str, **detail) -> None:
        event = {"label": label, "stage": stage, "state": state, **detail}
        try:
            self.report.setdefault("tunSubstages", []).append(event)
            self.write_report()
            print("CUSTOM_TUN_SUBSTAGE " + json.dumps(event, ensure_ascii=True), flush=True)
        except Exception as error:
            print("DIAGNOSTIC_WRITE_ERROR " + type(error).__name__, flush=True)

    def port_failure(self, label: str) -> None:
        result = {"label": label, "candidateTcpBindCount": PORT_OBSERVATION["count"],
                  "firstCandidateTcpPorts": list(PORT_OBSERVATION["firstPorts"]),
                  "lastCandidateTcpPort": PORT_OBSERVATION["lastPort"], "ranges": []}
        for kind in ("tcp", "udp"):
            for query in ("excludedportrange", "dynamicport"):
                command = ["netsh.exe", "interface", "ipv4", "show", query,
                           "protocol=" + kind if query == "excludedportrange" else kind]
                row = {"protocol": kind, "query": query}
                try:
                    completed = subprocess.run(command, capture_output=True, timeout=10, check=False,
                        creationflags=subprocess.CREATE_NO_WINDOW)
                    row.update({"exitCode": completed.returncode,
                        "stdout": completed.stdout[:16384].decode("utf-8", errors="replace"),
                        "stderr": completed.stderr[:2048].decode("utf-8", errors="replace"),
                        "truncated": len(completed.stdout) > 16384 or len(completed.stderr) > 2048})
                except Exception as error:
                    row["errorType"] = type(error).__name__
                result["ranges"].append(row)
        try:
            self.report.setdefault("portAllocationDiagnostics", []).append(result)
            self.write_report()
            (self.artifacts / "port-allocation-diagnostics.json").write_text(
                json.dumps(self.report["portAllocationDiagnostics"], ensure_ascii=True, indent=2) + "\n",
                encoding="utf-8")
            print("PORT_ALLOCATION_DIAGNOSTIC " + json.dumps(result, ensure_ascii=True), flush=True)
        except Exception as error:
            print("PORT_DIAGNOSTIC_WRITE_ERROR " + type(error).__name__, flush=True)

    def observed_call(self, label, stage, original, *args, **kwargs):
        started = time.monotonic()
        self.observation(label, stage, "entered")
        if stage == "source":
            PORT_OBSERVATION.update(activeThread=threading.get_ident(), count=0, firstPorts=[], lastPort=None)
        try:
            result = original(*args, **kwargs)
        except Exception as error:
            self.observation(label, stage, "failed", elapsedMs=round((time.monotonic() - started) * 1000, 2),
                             exception=exception_observation(error))
            if stage == "source" and type(error) is RuntimeError and str(error) == "no_available_tcp_udp_fixture_port":
                self.port_failure(label)
            raise
        else:
            self.observation(label, stage, "completed", elapsedMs=round((time.monotonic() - started) * 1000, 2))
            return result
        finally:
            if stage == "source":
                PORT_OBSERVATION["activeThread"] = None

    @contextmanager
    def trace_tun_calls(self, label: str):
        replacements = []
        def wrap(owner, name, stage):
            original = getattr(owner, name)
            def observed(*args, **kwargs):
                return self.observed_call(label, stage, original, *args, **kwargs)
            replacements.append((owner, name, original))
            setattr(owner, name, observed)
        try:
            wrap(custom, "RestrictedDesktop", "desktop_token")
            wrap(custom, "SocksFixture", "socks_fixture")
            wrap(NativeTunIdentity, "require_unused", "require_unused")
            wrap(TunUpgradeAcceptance, "source", "source")
            wrap(TunUpgradeAcceptance, "configure", "configure")
            wrap(TunUpgradeAcceptance, "launch_gui", "launch_gui")
            yield
        finally:
            for owner, name, original in reversed(replacements):
                setattr(owner, name, original)

    def tun_lifecycle(self, label: str, *, check_desktop_update: bool = False) -> None:
        with self.trace_tun_calls(label):
            super().tun_lifecycle(label, check_desktop_update=check_desktop_update)

    def load_candidate(self) -> None:
        super().load_candidate()
        require(self.info["uiCommit"] == SOURCE and self.info["desktopVersion"] == "0.2.14"
                and digest(self.candidate) == INSTALLER_SHA256, "diagnostic_candidate_not_exact_sixth_ci_artifact")

    def custom_core_update(self, target: Path) -> None:
        require(not self.report["skippedCases"], "diagnostic_skip_called_more_than_once")
        skipped = {"case": SKIPPED_CASE, "reason": SKIP_REASON, "plannedTarget": str(target)}
        self.report["skippedCases"].append(skipped)
        self.write_report()
        print("DIAGNOSTIC_ONLY_SKIP " + json.dumps(skipped, ensure_ascii=True), flush=True)


def main() -> int:
    os.chdir(ROOT)
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
    artifacts = ROOT / "artifacts" / "verification"
    artifacts.mkdir(parents=True, exist_ok=True)
    acceptance = None
    original_run_finished = False

    def record_failure(stage: str, error: Exception) -> None:
        record = {"stage": stage, "code": failure_code(error), "exception": exception_observation(error)}
        with (artifacts / "sixth-custom-exceptions.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(record, ensure_ascii=True) + "\n")
        print("CUSTOM_DIAGNOSTIC_EXCEPTION " + json.dumps(record, ensure_ascii=True), flush=True)
        if acceptance is not None:
            acceptance.report["diagnostics"].append(record)

    try:
        require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true",
                "disposable_github_windows_runner_required")
        require(ctypes.windll.shell32.IsUserAnAdmin(), "elevated_runner_required_for_custom_install")
        require(SKIPPED_CASE in REQUIRED_CASES, "diagnostic_skip_not_a_known_original_case")
        sys.addaudithook(observe_bind)
        acceptance = DiagnosticAcceptance(artifacts)
        acceptance.report.update({"passed": False, "partialDiagnostic": True, "fullSuiteExecuted": False,
            "diagnosticScope": "original CustomInstallAcceptance.run except custom_core_update",
            "plannedSkippedCases": [{"case": SKIPPED_CASE, "reason": SKIP_REASON}], "skippedCases": [],
            "diagnosticRequiredCases": sorted(REQUIRED_CASES - {SKIPPED_CASE}), "candidateSource": SOURCE,
            "candidateRunId": 45672045725, "candidateArtifactId": 15052215832,
            "diagnosticCommit": os.environ["GITHUB_SHA"]})
        acceptance.write_report()
        acceptance.run()
        original_run_finished = True
    except Exception as error:
        record_failure(acceptance.stage if acceptance else "preflight", error)
    finally:
        if acceptance is not None:
            try:
                acceptance.cleanup()
            except Exception as error:
                record_failure("cleanup", error)
            acceptance.report["passed"] = False
            skipped = [row["case"] for row in acceptance.report["skippedCases"]]
            acceptance.report["diagnosticPassed"] = (original_run_finished and skipped == [SKIPPED_CASE]
                and SKIPPED_CASE not in acceptance.report["tests"]
                and all(acceptance.report["tests"].get(name) is True for name in REQUIRED_CASES - {SKIPPED_CASE})
                and not acceptance.report["diagnostics"])
            acceptance.write_report()
            print(json.dumps({key: acceptance.report[key] for key in ("partialDiagnostic", "fullSuiteExecuted",
                "passed", "diagnosticPassed", "plannedSkippedCases", "skippedCases", "tests")},
                ensure_ascii=True, indent=2), flush=True)
    return 0 if acceptance is not None and acceptance.report.get("diagnosticPassed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
