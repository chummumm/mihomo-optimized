$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This integration setup is restricted to disposable GitHub Windows runners.' }
@'
import ctypes
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

sys.path.insert(0, str(Path("desktop/tests").resolve()))
from upgrade_smoke import digest, registration, service_snapshot
from windows_smoke import child_environment, require

require(sys.platform == "win32" and ctypes.windll.shell32.IsUserAnAdmin(), "fresh_setup_requires_elevated_windows")
root = Path.cwd()
distribution = root / "artifacts/desktop"
info_path = distribution / "build-info.json"
info = json.loads(info_path.read_text(encoding="utf-8-sig"))
version = json.loads((root / "desktop/app.json").read_text(encoding="utf-8"))["version"]
require(re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version), "fresh_setup_version_invalid")
require(info.get("desktopVersion") == version and info.get("desktopFramework") == "WPF"
        and info.get("uiCommit") == os.environ["GITHUB_SHA"], "fresh_setup_candidate_source_mismatch")
installer = distribution / f"mihomo-desktop-{version}-windows-x64-setup.exe"
expected_hashes = [line.split("  ", 1)[0]
    for line in (distribution / "SHA256SUMS").read_text(encoding="utf-8").splitlines()
    if line.endswith("  " + installer.name)]
require(len(expected_hashes) == 1 and re.fullmatch(r"[a-f0-9]{64}", expected_hashes[0])
        and digest(installer) == expected_hashes[0], "fresh_setup_installer_hash_mismatch")

install_dir = Path(os.environ["ProgramFiles"]) / "Mihomo Optimized"
require(registration() is None and registration(32) is None, "fresh_setup_preexisting_registration")
require(service_snapshot().get("winError") == 1060, "fresh_setup_preexisting_service")
require(not install_dir.exists(), "fresh_setup_preexisting_directory")
result = subprocess.run([str(installer), "/S"], cwd=distribution, env=child_environment(),
    stdin=subprocess.DEVNULL, capture_output=True, timeout=180, creationflags=subprocess.CREATE_NO_WINDOW)
require(result.returncode == 0, "fresh_setup_installer_failed")

expected = info.get("runtimeFiles")
require(isinstance(expected, dict) and bool(expected), "fresh_setup_runtime_manifest_missing")
for name, checksum in expected.items():
    relative = PurePosixPath(name)
    require(bool(relative.parts) and not relative.is_absolute() and ".." not in relative.parts
            and "\\" not in name and ":" not in name, "fresh_setup_runtime_path_invalid")
    path = install_dir / name
    require(path.is_file() and digest(path) == checksum, "fresh_setup_runtime_hash_mismatch")
installed_info = install_dir / "resources/build-info.json"
require(installed_info.is_file() and digest(installed_info) == digest(info_path), "fresh_setup_installed_source_mismatch")
runtime_files = [path for path in install_dir.rglob("*") if path.is_file()]
require({path.relative_to(install_dir).as_posix() for path in runtime_files}
        == set(expected) | {"resources/build-info.json", "uninstall.exe"}, "fresh_setup_runtime_inventory_mismatch")
require(not any(re.search(r"webview2|tauri|wry|chrome_elf|msedge|libcef|resources\.pak", path.name, re.IGNORECASE)
                for path in runtime_files), "fresh_setup_browser_runtime_present")
application = install_dir / "mihomo-optimized.exe"
require(application.is_file() and (install_dir / "mihomo-desktop-host.exe").is_file(), "fresh_setup_application_missing")
record = registration()
require(record and record["DisplayVersion"] == version
        and Path(record["InstallLocation"]).resolve() == install_dir.resolve()
        and registration(32) is None, "fresh_setup_registration_mismatch")

report = {"passed": True, "mode": "fresh_install", "framework": "WPF", "selfContained": True,
          "browserRuntime": False, "installerExitCode": result.returncode, "application": str(application),
          "source": info["uiCommit"], "installerSha256": expected_hashes[0], "verifiedRuntimeFiles": len(expected)}
out = root / "artifacts/verification"
out.mkdir(parents=True, exist_ok=True)
(out / "installation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as environment:
    environment.write(f"MIHOMO_TEST_APP={application}\n")
print(json.dumps(report, indent=2))
'@ | python -
if ($LASTEXITCODE -ne 0) { throw 'Fresh candidate installation or runtime verification failed.' }
