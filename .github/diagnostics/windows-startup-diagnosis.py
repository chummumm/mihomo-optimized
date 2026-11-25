#!/usr/bin/env python3
"""Single-run observation of the unchanged Windows shell and UIA contracts."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

SOURCE = "e544948cca3449c4ea8a6f37e3d0df2be7084ed8"
SAMPLER = r'''
param([int]$RootPid, [string]$OutputPath, [string]$StopPath, [string]$Workspace)
$ErrorActionPreference = 'Stop'
$clock = [Diagnostics.Stopwatch]::StartNew()
$known = @{}
$paths = @(
  'desktop/tests/shell/obj/project.assets.json',
  'desktop/tests/shell/bin/Release/net10.0-windows/ShellTests.dll',
  'desktop/tests/shell/bin/Release/net10.0-windows/ShellTests.exe',
  'artifacts/verification/shell-contract/report.json',
  'artifacts/verification/uia-json-contract.json'
)
while (-not (Test-Path -LiteralPath $StopPath)) {
  try {
    $all = @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name,CommandLine,CreationDate,KernelModeTime,UserModeTime)
    $selected = @{}
    foreach ($entry in $all) {
      $id = [int]$entry.ProcessId
      $created = [string]$entry.CreationDate
      if (($id -eq $RootPid -and (-not $known.ContainsKey($id) -or $known[$id] -eq $created)) -or
          ($known.ContainsKey($id) -and $known[$id] -eq $created)) {
        $selected[$id] = $entry
      }
    }
    do {
      $changed = $false
      foreach ($entry in $all) {
        $id = [int]$entry.ProcessId
        if (-not $selected.ContainsKey($id) -and $selected.ContainsKey([int]$entry.ParentProcessId)) {
          $selected[$id] = $entry
          $changed = $true
        }
      }
    } while ($changed)
    $processes = @($selected.Values | Sort-Object ProcessId | ForEach-Object {
      $known[[int]$_.ProcessId] = [string]$_.CreationDate
      $command = [string]$_.CommandLine
      # Only this owned process tree is emitted; never enumerate environment.
      $command = $command -replace '(?i)(--?(?:token|password|secret|api-key)(?:=|\s+))\S+', '$1[redacted]'
      if ($command.Length -gt 4096) { $command = $command.Substring(0,4096) + '[truncated]' }
      [ordered]@{ pid=[int]$_.ProcessId; parentPid=[int]$_.ParentProcessId; name=$_.Name;
        created=[string]$_.CreationDate; command=$command;
        cpuSeconds=([double]$_.KernelModeTime + [double]$_.UserModeTime) / 10000000 }
    })
    $files = @($paths | ForEach-Object {
      $relative = $_
      $item = Get-Item -LiteralPath (Join-Path $Workspace $relative) -ErrorAction SilentlyContinue
      if ($null -eq $item) { [ordered]@{ path=$relative; exists=$false } }
      else { [ordered]@{ path=$relative; exists=$true; bytes=$item.Length;
        modifiedUtc=$item.LastWriteTimeUtc.ToString('o') } }
    })
    [ordered]@{ elapsedSeconds=$clock.Elapsed.TotalSeconds; rootPid=$RootPid;
      processes=$processes; files=$files } | ConvertTo-Json -Depth 8 -Compress |
      Add-Content -LiteralPath $OutputPath -Encoding UTF8
  } catch {
    [ordered]@{ elapsedSeconds=$clock.Elapsed.TotalSeconds; rootPid=$RootPid;
      observerError=$_.Exception.GetType().FullName; message=$_.Exception.Message } |
      ConvertTo-Json -Compress | Add-Content -LiteralPath $OutputPath -Encoding UTF8
  }
  Start-Sleep -Seconds 2
}
'''


def run_observed(root: Path, artifacts: Path, label: str, command: list[str], budget: int) -> dict:
    result = {"command": command, "budgetSeconds": budget, "timedOut": False}
    stop = artifacts / (label + "-observer.stop")
    observer = process = None
    started = time.monotonic()
    with (artifacts / (label + "-stdout.log")).open("wb") as out, \
            (artifacts / (label + "-stderr.log")).open("wb") as err, \
            (artifacts / (label + "-observer.log")).open("wb") as observed:
        try:
            process = subprocess.Popen(command, cwd=root, stdin=subprocess.DEVNULL, stdout=out, stderr=err)
            result["rootPid"] = process.pid
            try:
                observer = subprocess.Popen(["powershell.exe", "-NoProfile", "-NonInteractive",
                    "-ExecutionPolicy", "Bypass", "-File", str(artifacts / "observe-owned-tree.ps1"),
                    "-RootPid", str(process.pid), "-OutputPath", str(artifacts / (label + "-processes.jsonl")),
                    "-StopPath", str(stop), "-Workspace", str(root)],
                    cwd=root, stdin=subprocess.DEVNULL, stdout=observed, stderr=observed)
            except Exception as error:
                result["observerStartError"] = str(error)
            print(json.dumps({"event": "started", "label": label, **result}), flush=True)
            try:
                result["exitCode"] = process.wait(timeout=max(0, budget - (time.monotonic() - started)))
            except subprocess.TimeoutExpired:
                result["timedOut"] = True
                result["deadlineElapsedSeconds"] = round(time.monotonic() - started, 3)
                # Only terminate the still-live process this harness started and its children.
                if process.poll() is None:
                    killed = subprocess.run(["taskkill.exe", "/PID", str(process.pid), "/T", "/F"],
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15, check=False)
                    result["treeKillExitCode"] = killed.returncode
                result["exitCode"] = process.wait(timeout=10)
        except Exception as error:
            result.update({"harnessErrorType": type(error).__name__, "harnessError": str(error)})
        finally:
            stop.write_text("stop\n", encoding="utf-8")
            if observer is not None:
                try:
                    observer.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    observer.kill()
                    observer.wait(timeout=5)
                    result["observerForcedStop"] = True
    result["elapsedSeconds"] = round(time.monotonic() - started, 3)
    for stream in ("stdout", "stderr"):
        path = artifacts / (label + "-" + stream + ".log")
        result[stream + "Bytes"] = path.stat().st_size
        # Normal verbosity only: keep full text in the artifact, do not dump it to the job console.
    print(json.dumps({"event": "finished", "label": label, **result}), flush=True)
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", type=Path, required=True)
    args = parser.parse_args()
    root = args.workspace.resolve()
    if sys.platform != "win32" or os.environ.get("GITHUB_ACTIONS") != "true":
        raise RuntimeError("Disposable GitHub Windows runner required")
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=root, capture_output=True,
                          text=True, check=True, timeout=10).stdout.strip()
    if head != SOURCE:
        raise RuntimeError("Diagnostic checkout must match the exact production source")
    artifacts = root / "artifacts" / "verification" / "startup-diagnosis"
    artifacts.mkdir(parents=True, exist_ok=True)
    (artifacts / "observe-owned-tree.ps1").write_text(SAMPLER, encoding="utf-8-sig")
    report = {"partialDiagnostic": True, "fullSuiteExecuted": False, "passed": False,
              "source": SOURCE, "diagnosticCommit": os.environ.get("GITHUB_SHA"),
              "requestedSdk": "10.0.402", "dotnetPath": shutil.which("dotnet"),
              "windowsVersion": list(sys.getwindowsversion()),
              "runnerImage": os.environ.get("ImageOS"), "runnerImageVersion": os.environ.get("ImageVersion"),
              "runnerRegion": os.environ.get("RUNNER_REGION"),
              "regionFallback": "Azure Region is retained in the GitHub Set up job log",
              "automaticRetries": 0, "binaryLog": "omitted to avoid environment/import capture"}
    print(json.dumps({"event": "provenance", **report}), flush=True)
    report["shell"] = run_observed(root, artifacts, "shell", ["dotnet", "run", "--project",
        "desktop/tests/shell/ShellTests.csproj", "-c", "Release", "--verbosity", "normal"], 180)
    # The production entry point retains its own unchanged 15-second PowerShell
    # timeout and all encoder assertions, regardless of the shell test outcome.
    report["uia"] = run_observed(root, artifacts, "uia", [sys.executable,
        "desktop/tests/windows_smoke.py", "--uia-json-contract"], 45)
    report["diagnosticPassed"] = all(report[name].get("exitCode") == 0
        and not report[name].get("timedOut") and not report[name].get("harnessError") for name in ("shell", "uia"))
    (artifacts / "summary.json").write_text(json.dumps(report, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True, indent=2), flush=True)
    return 0 if report["diagnosticPassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
