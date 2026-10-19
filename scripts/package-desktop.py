#!/usr/bin/env python3
"""Assemble exactly the installed runtime layout in the portable ZIP."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TARGET = "x86_64-pc-windows-msvc"


def digest(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def checksums(directory):
    lines = [f"{digest(path)}  {path.name}" for path in sorted(directory.iterdir()) if path.is_file() and path.name != "SHA256SUMS"]
    (directory / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--checksums-only", action="store_true")
    args = parser.parse_args()
    output = ROOT / "artifacts/desktop"
    output.mkdir(parents=True, exist_ok=True)
    if args.checksums_only:
        checksums(output)
        return
    config = json.loads((ROOT / "src-tauri/tauri.conf.json").read_text())
    version = config["version"]
    release = ROOT / "src-tauri/target" / TARGET / "release"
    prefix = f"mihomo-desktop-{version}-windows-x64"
    # The compilation cache can contain installers from older versions. Tauri
    # 2.12.3 names NSIS output by product, version and architecture; select the
    # exact current build instead of treating cached installers as candidates.
    setup = release / "bundle/nsis" / f"{config['productName']}_{version}_x64-setup.exe"
    if not setup.is_file():
        raise ValueError(f"Current-version NSIS installer is missing: {setup.name}")
    shutil.copy2(setup, output / f"{prefix}-setup.exe")
    with zipfile.ZipFile(output / f"{prefix}-portable.zip", "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
        bundle.write(release / "mihomo-optimized.exe", "Mihomo Optimized/mihomo-optimized.exe")
        for name in ["mihomo-core", "mihomo-desktop-service", "mihomo-update-verify"]:
            bundle.write(ROOT / "src-tauri/binaries" / f"{name}-{TARGET}.exe", f"Mihomo Optimized/{name}.exe")
        for path in sorted((ROOT / "src-tauri/resources").rglob("*")):
            if path.is_file():
                bundle.write(path, "Mihomo Optimized/resources/" + str(path.relative_to(ROOT / "src-tauri/resources")).replace("\\", "/"))
        bundle.write(ROOT / "desktop/README.zh-CN.md", "Mihomo Optimized/README.zh-CN.md")
    shutil.copy2(ROOT / "src-tauri/resources/build-info.json", output / "build-info.json")
    shutil.copy2(ROOT / "desktop/README.zh-CN.md", output / "README.zh-CN.md")
    checksums(output)
    print(json.dumps({"version": version, "tag": f"mihomo-desktop-v{version}", "artifacts": [path.name for path in sorted(output.iterdir())]}, indent=2))


if __name__ == "__main__":
    main()
