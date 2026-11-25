#!/usr/bin/env python3
"""Partial diagnostic only: original same-volume install and full TUN lifecycle."""

import ctypes
import json
import os
from pathlib import Path
import sys
import traceback
import uuid

ROOT = Path(os.environ["GITHUB_WORKSPACE"])
sys.path.insert(0, str(ROOT / "desktop" / "tests"))

from custom_install_smoke import CustomInstallAcceptance, set_directory_security
from upgrade_smoke import PRODUCT, digest, failure_code, registration, service_snapshot
from upgrade_tun_smoke import TunUpgradeAcceptance
from windows_smoke import require


SOURCE = "31b974cd2cb7edc1f12d5b6d7cd0963ad121288e"
INSTALLER_SHA256 = "d9ffb9a5062401c271ac8771a4261c3b44ab884d5185f0aceaf38f8ef087ee81"


def failure_trace(error: Exception) -> str:
    # Follow the existing TUN diagnostic redaction policy. Reading traceback
    # locals only discovers the fixture's known secrets; locals are never logged.
    secrets = set()
    current = error.__traceback__
    while current is not None:
        for name in ("self", "tun"):
            value = current.tb_frame.f_locals.get(name)
            if isinstance(value, TunUpgradeAcceptance):
                secrets.update(getattr(value, "secrets", set()))
        current = current.tb_next
    text = "".join(traceback.format_exception(error))
    for secret in secrets:
        if secret:
            text = text.replace(secret, "[redacted]")
    return "".join(char for char in text if char in "\n\t" or ord(char) >= 32)


def main() -> int:
    import win32api
    import win32security

    os.chdir(ROOT)
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
    artifacts = ROOT / "artifacts" / "verification"
    artifacts.mkdir(parents=True, exist_ok=True)
    acceptance = None
    lifecycle_passed = False

    def record_failure(stage: str, error: Exception) -> None:
        text = failure_trace(error)
        with (artifacts / "custom-tun-traceback.log").open("a", encoding="utf-8") as stream:
            stream.write(stage + "\n" + text + "\n")
        print(text, file=sys.stderr, flush=True)
        if acceptance is not None:
            acceptance.report["diagnostics"].append({
                "stage": stage, "code": failure_code(error), "traceback": text,
            })

    try:
        require(sys.platform == "win32" and os.environ.get("GITHUB_ACTIONS") == "true",
                "disposable_github_windows_runner_required")
        require(ctypes.windll.shell32.IsUserAnAdmin(), "elevated_runner_required_for_custom_install")
        acceptance = CustomInstallAcceptance(artifacts)
        acceptance.report.update({
            "passed": False,
            "partialDiagnostic": True,
            "fullSuiteExecuted": False,
            "diagnosticScope": "original same-volume install_at and complete tun_lifecycle",
            "candidateSource": SOURCE,
            "candidateRunId": 45650772797,
            "candidateArtifactId": 15040982295,
            "diagnosticCommit": os.environ["GITHUB_SHA"],
        })
        acceptance.write_report()

        # These are the original fixture's safety/identity preconditions, with
        # no pre-existing installation, service or data adopted by this harness.
        require(registration() is None and registration(32) is None
                and not acceptance.default_directory.exists(), "preexisting_application_installation")
        require(service_snapshot().get("winError") == 1060, "preexisting_mihomo_service")
        acceptance.owned_data = [acceptance.config.parent, acceptance.owner_file.parent.parent]
        require(all(not path.exists() for path in acceptance.owned_data), "preexisting_mihomo_data")
        require(not acceptance.fixture_root.exists(), "preexisting_path_fixture")
        token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32security.TOKEN_QUERY)
        try:
            acceptance.sid = win32security.ConvertSidToStringSid(
                win32security.GetTokenInformation(token, win32security.TokenUser)[0])
        finally:
            token.Close()
        acceptance.load_candidate()
        require(acceptance.info["uiCommit"] == SOURCE
                and acceptance.info["desktopVersion"] == "0.2.14"
                and digest(acceptance.candidate) == INSTALLER_SHA256,
                "diagnostic_candidate_not_exact_third_ci_artifact")
        acceptance.mutations_started = True
        acceptance.fixture_root.mkdir()
        set_directory_security(acceptance.fixture_root)
        acceptance.config.parent.mkdir()
        acceptance.config.write_text(
            "# custom-install-" + uuid.uuid4().hex + "\nmode: rule\nrules:\n  - MATCH,DIRECT\n",
            encoding="utf-8")
        acceptance.marker_hash = digest(acceptance.config)

        # Keep every assertion in these original methods, including actual
        # forwarding, two restarts, process handoff, source preservation and
        # the real uninstaller. This is explicitly only one suite scenario.
        acceptance.install_at(acceptance.fixture_root / PRODUCT, "same_volume_install")
        acceptance.tun_lifecycle("same_volume_tun")
        lifecycle_passed = True
    except Exception as error:
        record_failure(acceptance.stage if acceptance else "preflight", error)
    finally:
        if acceptance is not None:
            try:
                acceptance.cleanup()
            except Exception as error:
                record_failure("cleanup", error)
            acceptance.report["passed"] = False
            acceptance.report["diagnosticPassed"] = (
                lifecycle_passed
                and acceptance.report["tests"].get("same_volume_tun") is True
                and acceptance.report["tests"].get("final_cleanup") is True
                and not acceptance.report["diagnostics"]
            )
            acceptance.write_report()
            print(json.dumps({key: acceptance.report[key] for key in
                  ("partialDiagnostic", "fullSuiteExecuted", "passed", "diagnosticPassed", "tests")},
                  ensure_ascii=True, indent=2), flush=True)
    return 0 if acceptance is not None and acceptance.report.get("diagnosticPassed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
