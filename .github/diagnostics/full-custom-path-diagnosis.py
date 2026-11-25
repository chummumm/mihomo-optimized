#!/usr/bin/env python3
"""Original custom-path suite, with one explicitly unavailable baseline skipped."""

import ctypes
import json
import os
from pathlib import Path
import sys
import traceback

ROOT = Path(os.environ["GITHUB_WORKSPACE"])
sys.path.insert(0, str(ROOT / "desktop" / "tests"))

from custom_install_smoke import CustomInstallAcceptance, REQUIRED_CASES
from upgrade_smoke import digest, failure_code
from upgrade_tun_smoke import TunUpgradeAcceptance
from windows_smoke import require


SOURCE = "882d416355513b55409a879547d33ed825441418"
INSTALLER_SHA256 = "445cffd0ed5534a05c73008decb2e8f4e21b56d3c19669281312b277f4f8c14e"
SKIPPED_CASE = "custom_core_update"
SKIP_REASON = "The source-built core-update baseline was not uploaded with this candidate artifact."


class DiagnosticAcceptance(CustomInstallAcceptance):
    def load_candidate(self) -> None:
        super().load_candidate()
        require(self.info["uiCommit"] == SOURCE and self.info["desktopVersion"] == "0.2.14"
                and digest(self.candidate) == INSTALLER_SHA256,
                "diagnostic_candidate_not_exact_fourth_ci_artifact")

    def custom_core_update(self, target: Path) -> None:
        # This is the only scenario omitted. Do not mark it passed, construct
        # its missing baseline, or change any production fixture or gate.
        require(not self.report["skippedCases"], "diagnostic_skip_called_more_than_once")
        skipped = {"case": SKIPPED_CASE, "reason": SKIP_REASON, "plannedTarget": str(target)}
        self.report["skippedCases"].append(skipped)
        self.write_report()
        print("DIAGNOSTIC_ONLY_SKIP " + json.dumps(skipped, ensure_ascii=True), flush=True)


def failure_trace(error: Exception) -> str:
    # Use the existing TUN fixture's known-secret redaction policy without
    # retaining or displaying stack locals or unfiltered protocol responses.
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
    os.chdir(ROOT)
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
    artifacts = ROOT / "artifacts" / "verification"
    artifacts.mkdir(parents=True, exist_ok=True)
    acceptance = None
    original_run_finished = False

    def record_failure(stage: str, error: Exception) -> None:
        text = failure_trace(error)
        with (artifacts / "full-custom-path-traceback.log").open("a", encoding="utf-8") as stream:
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
        require(SKIPPED_CASE in REQUIRED_CASES, "diagnostic_skip_not_a_known_original_case")
        acceptance = DiagnosticAcceptance(artifacts)
        acceptance.report.update({
            "passed": False,
            "partialDiagnostic": True,
            "fullSuiteExecuted": False,
            "diagnosticScope": "original CustomInstallAcceptance.run except custom_core_update",
            "plannedSkippedCases": [{"case": SKIPPED_CASE, "reason": SKIP_REASON}],
            "skippedCases": [],
            "diagnosticRequiredCases": sorted(REQUIRED_CASES - {SKIPPED_CASE}),
            "candidateSource": SOURCE,
            "candidateRunId": 45658312465,
            "candidateArtifactId": 15046225380,
            "diagnosticCommit": os.environ["GITHUB_SHA"],
        })
        acceptance.write_report()
        # The original run method owns every preflight, path case, real
        # installer/service/TUN operation, repair and removal assertion.
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
            acceptance.report["diagnosticPassed"] = (
                original_run_finished and skipped == [SKIPPED_CASE]
                and SKIPPED_CASE not in acceptance.report["tests"]
                and all(acceptance.report["tests"].get(name) is True
                        for name in REQUIRED_CASES - {SKIPPED_CASE})
                and not acceptance.report["diagnostics"]
            )
            acceptance.write_report()
            print(json.dumps({key: acceptance.report[key] for key in
                  ("partialDiagnostic", "fullSuiteExecuted", "passed", "diagnosticPassed",
                   "plannedSkippedCases", "skippedCases", "tests")},
                  ensure_ascii=True, indent=2), flush=True)
    return 0 if acceptance is not None and acceptance.report.get("diagnosticPassed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
