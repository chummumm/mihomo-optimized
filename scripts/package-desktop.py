#!/usr/bin/env python3
"""Package the exact native runtime used by NSIS, with every DLL accounted for."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]


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
    application = json.loads((ROOT / "desktop/app.json").read_text())
    version = application["version"]
    runtime = ROOT / "dist-native"
    info = json.loads((runtime / "resources/build-info.json").read_text())
    if info.get("desktopVersion") != version or info.get("desktopFramework") != "WPF":
        raise ValueError("The native runtime belongs to another application version")
    expected = info.get("runtimeFiles", {})
    actual = {path.relative_to(runtime).as_posix(): digest(path)
              for path in runtime.rglob("*") if path.is_file()
              and path != runtime / "resources/build-info.json"}
    if not expected or actual != expected:
        raise ValueError("The native runtime no longer matches its build manifest")
    prefix = f"mihomo-desktop-{version}-windows-x64"
    if not (output / f"{prefix}-setup.exe").is_file():
        raise ValueError("Current-version NSIS installer is missing")
    with zipfile.ZipFile(output / f"{prefix}-portable.zip", "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
        for path in sorted(runtime.rglob("*")):
            if path.is_file():
                bundle.write(path, application["productName"] + "/" + path.relative_to(runtime).as_posix())
    shutil.copy2(runtime / "resources/build-info.json", output / "build-info.json")
    shutil.copy2(ROOT / "desktop/README.zh-CN.md", output / "README.zh-CN.md")
    checksums(output)
    print(json.dumps({"version": version, "tag": f"mihomo-desktop-v{version}", "artifacts": [path.name for path in sorted(output.iterdir())]}, indent=2))


if __name__ == "__main__":
    main()
