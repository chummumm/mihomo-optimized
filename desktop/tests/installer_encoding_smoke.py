#!/usr/bin/env python3
"""Exercise the real helper-to-NSIS error boundary after the Windows build."""

from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
EXPECTED = "invalid_service_action: 不支持的服务启动参数"
INVALID_ACTION = "--encoding-contract-invalid-action"
PROBE = r'''
Unicode true
RequestExecutionLevel admin
Name "Mihomo installer encoding contract"
OutFile "@OUTPUT@"
SilentInstall silent
SilentUnInstall silent
Var PassiveMode
!include "..\src-tauri\windows\hooks.nsh"

Function .onInit
  StrCpy $PassiveMode 1
  StrCpy $MihomoMaintenanceDirectory ""
FunctionEnd
Function un.onInit
  StrCpy $PassiveMode 1
  StrCpy $MihomoMaintenanceDirectory ""
FunctionEnd

Section "Install encoding probe"
  WriteUninstaller "@UNINSTALLER@"
  Call MihomoEnsureMaintenance
  !insertmacro MihomoRunMaintenance "" "@ACTION@" "encoding_probe"
  SetErrorLevel 2
SectionEnd
Section "Uninstall"
  Call un.MihomoEnsureMaintenance
  !insertmacro MihomoRunMaintenance "un." "@ACTION@" "encoding_probe"
  SetErrorLevel 2
SectionEnd

Function .onInstFailed
  FileOpen $0 "@INSTALL_RESULT@" w
  FileWriteUTF16LE /BOM $0 "$MihomoMaintenanceStage$\n$MihomoMaintenanceError$\n$MihomoMaintenanceDetails"
  FileClose $0
  Call MihomoCleanupMaintenance
FunctionEnd
Function un.onUninstFailed
  FileOpen $0 "@UNINSTALL_RESULT@" w
  FileWriteUTF16LE /BOM $0 "$MihomoMaintenanceStage$\n$MihomoMaintenanceError$\n$MihomoMaintenanceDetails"
  FileClose $0
  Call un.MihomoCleanupMaintenance
FunctionEnd
'''


def require(condition: bool, code: str) -> None:
    if not condition:
        raise AssertionError(code)


def read_report(path: Path) -> str:
    data = path.read_bytes()
    require(data.startswith(b"\xff\xfe"), "installer_error_report_missing_utf16le_bom")
    return data[2:].decode("utf-16-le", errors="strict")


def main() -> None:
    require(os.name == "nt", "installer_encoding_requires_windows")
    require(ctypes.windll.shell32.IsUserAnAdmin() != 0, "installer_encoding_requires_elevation")
    helper = ROOT / "dist-native/mihomo-desktop-service.exe"
    compiler = shutil.which("makensis")
    require(helper.is_file() and compiler is not None, "installer_encoding_requires_built_payload_and_nsis")

    result = subprocess.run([str(helper), INVALID_ACTION], capture_output=True, timeout=30)
    require(result.returncode == 1, "ordinary_helper_error_exit_changed")
    require(result.stderr.decode("utf-8", errors="strict").strip() == EXPECTED,
            "ordinary_helper_error_not_utf8")

    artifacts = ROOT / "artifacts/verification"
    artifacts.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="安装编码-", dir=artifacts) as temporary:
        directory = Path(temporary)
        report = directory / "维护错误.txt"
        result = subprocess.run([str(helper), "--nsis-error-file", str(report), INVALID_ACTION],
                                capture_output=True, timeout=30)
        require(result.returncode == 1 and result.stdout == result.stderr == b"",
                "nsis_helper_error_transport_changed")
        require(read_report(report) == EXPECTED, "nsis_helper_error_not_utf16le")
        original = report.read_bytes()
        result = subprocess.run([str(helper), "--nsis-error-file", str(report), INVALID_ACTION],
                                capture_output=True, timeout=30)
        require(result.returncode == 1
                and result.stderr.startswith(b"nsis_error_report_open_failed:")
                and report.read_bytes() == original, "nsis_helper_overwrote_existing_report")

        installer = directory / "encoding-setup.exe"
        uninstaller = directory / "encoding-uninstall.exe"
        installed_result = directory / "安装结果.txt"
        uninstalled_result = directory / "卸载结果.txt"
        source = PROBE
        for marker, value in {
            "OUTPUT": installer, "UNINSTALLER": uninstaller, "ACTION": INVALID_ACTION,
            "INSTALL_RESULT": installed_result, "UNINSTALL_RESULT": uninstalled_result,
        }.items():
            source = source.replace("@" + marker + "@", str(value).replace("$", "$$"))
        script = directory / "encoding.nsi"
        script.write_text(source, encoding="utf-8-sig")
        subprocess.run([compiler, "/V2", "/NOCD", str(script)], cwd=ROOT / "desktop",
                       check=True, timeout=60)
        for command, observed in (
            ([str(installer), "/S"], installed_result),
            # NSIS requires the unquoted _?= remainder to run in place and wait.
            (f'"{uninstaller}" /S _?={directory}', uninstalled_result),
        ):
            result = subprocess.run(command, capture_output=True, timeout=60)
            require(result.returncode == 1, "nsis_encoding_probe_did_not_preserve_failure")
            require(read_report(observed) == "encoding_probe\n1\n" + EXPECTED,
                    "nsis_error_details_changed_after_decode")

    evidence = {"passed": True, "windowsAnsiCodePage": ctypes.windll.kernel32.GetACP(),
                "ordinaryCliUtf8": True, "installerUtf16le": True,
                "uninstallerUtf16le": True, "existingReportPreserved": True}
    (artifacts / "installer-encoding.json").write_text(json.dumps(evidence, indent=2) + "\n",
                                                     encoding="utf-8")
    print(json.dumps(evidence))


if __name__ == "__main__":
    main()
