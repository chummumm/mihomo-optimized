#!/usr/bin/env python3
"""Publish the native UI and assemble one complete, browser-free Windows runtime."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tomllib
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
TARGET = "x86_64-pc-windows-msvc"


def run(*arguments, cwd=ROOT):
    subprocess.run(arguments, cwd=cwd, check=True)


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def nsis_text(value):
    if any(character in value for character in ('\r', '\n', '"', '$')):
        raise ValueError("Unsupported character in a packaged path")
    return value.replace('/', '\\')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--skip-installer", action="store_true", help="Assemble the complete application without requiring NSIS")
    parser.add_argument("--run", action="store_true", help="Launch the assembled native application after building")
    options = parser.parse_args()
    application = json.loads((ROOT / "desktop/app.json").read_text())
    version = application["version"]
    cargo = tomllib.loads((ROOT / "src-tauri/Cargo.toml").read_text())
    project = ET.parse(ROOT / "src-windows/Mihomo.Desktop.csproj")
    if cargo["package"]["version"] != version or project.findtext(".//Version") != version:
        raise ValueError("Native UI, host and package versions must agree")
    if os.name != "nt":
        raise ValueError("The distributable must be built on Windows")
    output = ROOT / "dist-native"
    if output.exists():
        shutil.rmtree(output)  # Only the fixed generated build directory.
    run("dotnet", "publish", "src-windows/Mihomo.Desktop.csproj", "--configuration", "Release",
        "--runtime", "win-x64", "--self-contained", "true", "--output", str(output),
        "-p:ContinuousIntegrationBuild=true", "-p:DebugType=None", "-p:DebugSymbols=false")
    run("cargo", "build", "--locked", "--release", "--target", TARGET,
        "--bin", "mihomo-desktop-host", cwd=ROOT / "src-tauri")
    host = ROOT / "src-tauri/target" / TARGET / "release/mihomo-desktop-host.exe"
    shutil.copy2(host, output / host.name)
    binaries = ROOT / "src-tauri/binaries"
    for name in ("mihomo-core", "mihomo-desktop-service", "mihomo-update-verify"):
        shutil.copy2(binaries / f"{name}-{TARGET}.exe", output / f"{name}.exe")
    shutil.copytree(ROOT / "src-tauri/resources", output / "resources")
    shutil.copy2(ROOT / "desktop/README.zh-CN.md", output / "README.zh-CN.md")
    info_path = output / "resources/build-info.json"
    info = json.loads(info_path.read_text())
    info["companions"][f"mihomo-desktop-host-{TARGET}.exe"] = digest(output / host.name)
    info["dotnetSdk"] = subprocess.check_output(["dotnet", "--version"], text=True).strip()
    info["desktopFramework"] = "WPF"
    files = sorted(path for path in output.rglob("*") if path.is_file())
    if not (output / application["executable"]).is_file():
        raise ValueError("Native application executable is missing")
    forbidden = ("webview2", "tauri", "wry", "chrome_elf", "msedge", "libcef", "resources.pak")
    if any(any(item in path.name.lower() for item in forbidden) for path in files):
        raise ValueError("A browser/Tauri runtime entered the native application package")
    for path in files:
        if path.is_symlink():
            raise ValueError("Packaged files cannot be symbolic links")
    info["runtimeFiles"] = {path.relative_to(output).as_posix(): digest(path)
                            for path in files if path != info_path}
    info_path.write_text(json.dumps(info, indent=2) + "\n", encoding="utf-8")
    generated = ROOT / "desktop/generated"
    generated.mkdir(parents=True, exist_ok=True)
    manifest = ["; Generated from dist-native. Delete only files shipped by this installer."]
    for path in files:
        relative = nsis_text(path.relative_to(output).as_posix())
        manifest.extend([f'Delete "$INSTDIR\\{relative}"',
                         f'IfFileExists "$INSTDIR\\{relative}" 0 +3',
                         'SetErrorLevel 1', f'Abort "Unable to remove {relative}. Close the application and retry."'])
    directories = sorted({parent for path in files for parent in path.relative_to(output).parents
                          if parent != Path('.')}, key=lambda path: len(path.parts), reverse=True)
    for directory in directories:
        manifest.append(f'RMDir "$INSTDIR\\{nsis_text(directory.as_posix())}"')
    (generated / "uninstall-files.nsh").write_text("\n".join(manifest) + "\n", encoding="utf-8")
    if not options.skip_installer:
        makensis = shutil.which("makensis") or str(Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "NSIS/makensis.exe")
        (ROOT / "artifacts/desktop").mkdir(parents=True, exist_ok=True)
        run(makensis, "/V3", f"/DAPP_VERSION={version}", "desktop/installer.nsi")
    print(json.dumps({"version": version, "framework": "WPF", "runtimeFiles": len(files),
                      "webviewRuntime": False}, indent=2))
    if options.run:
        run(str(output / application["executable"]), cwd=output)


if __name__ == "__main__":
    main()
