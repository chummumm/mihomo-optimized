#!/usr/bin/env python3
"""Stage pinned companion executables for the native Windows build."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tomllib
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TARGET = "x86_64-pc-windows-msvc"
BINARIES = ROOT / "src-tauri" / "binaries"
RESOURCES = ROOT / "src-tauri" / "resources"


def run(*args, cwd=ROOT, env=None):
    return subprocess.check_output(args, cwd=cwd, env=env, text=True).strip()


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def ensure_pe(path):
    with path.open("rb") as source:
        if source.read(2) != b"MZ":
            raise ValueError(f"Not a Windows executable: {path.name}")
        source.seek(0x3C)
        offset = int.from_bytes(source.read(4), "little")
        source.seek(offset)
        if source.read(4) != b"PE\0\0" or source.read(2) != b"\x64\x86":
            raise ValueError(f"Not a Windows x64 PE: {path.name}")


def download_cli():
    spec = json.loads((ROOT / "desktop/third-party.json").read_text())["githubCli"]
    cache = ROOT / "desktop-cache"
    cache.mkdir(exist_ok=True)
    archive = cache / f"gh-{spec['version']}-windows-amd64.zip"
    if not archive.exists() or sha256(archive) != spec["sha256"]:
        temporary = archive.with_suffix(".download")
        request = urllib.request.Request(spec["url"], headers={"User-Agent": "mihomo-desktop-builder"})
        try:
            with urllib.request.urlopen(request, timeout=120) as response, temporary.open("wb") as output:
                total = 0
                while block := response.read(1024 * 1024):
                    total += len(block)
                    if total > 150 * 1024 * 1024:
                        raise ValueError("Verifier archive exceeds the download limit")
                    output.write(block)
            if sha256(temporary) != spec["sha256"]:
                raise ValueError("GitHub CLI checksum mismatch")
            temporary.replace(archive)
        finally:
            temporary.unlink(missing_ok=True)
    destination = BINARIES / f"mihomo-update-verify-{TARGET}.exe"
    with zipfile.ZipFile(archive) as bundle:
        members = [member for member in bundle.infolist() if member.filename.endswith("/bin/gh.exe") or member.filename == "bin/gh.exe"]
        if len(members) != 1 or members[0].file_size > 200 * 1024 * 1024:
            raise ValueError("Unexpected verifier archive layout")
        with bundle.open(members[0]) as source, destination.open("wb") as output:
            shutil.copyfileobj(source, output)
        licenses = [member for member in bundle.infolist() if Path(member.filename).name.lower() == "license"]
        if not licenses:
            raise ValueError("Verifier archive is missing its license")
        (RESOURCES / "licenses/GitHub-CLI-LICENSE.txt").write_bytes(bundle.read(licenses[0]))
    ensure_pe(destination)
    return spec["version"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--core-dir", type=Path, required=True)
    parser.add_argument("--service", type=Path)
    parser.add_argument("--allow-local-core", action="store_true", help="Development only; marks build as dirty and cannot be used in release CI")
    args = parser.parse_args()
    core = args.core_dir.resolve()
    application = json.loads((ROOT / 'desktop/app.json').read_text())
    cargo = tomllib.loads((ROOT / 'src-tauri/Cargo.toml').read_text())
    if application['version'] != cargo['package']['version']:
        raise ValueError('Application and Cargo desktop versions must agree')
    spec = json.loads((ROOT / "desktop/core.json").read_text())
    if spec["repository"] != "chummumm/mihomo-optimized" or not re.fullmatch(r"[0-9a-f]{40}", spec["commit"]):
        raise ValueError("The desktop core must be pinned to a full commit in the project repository")
    actual = run("git", "rev-parse", "HEAD", cwd=core)
    dirty = bool(run("git", "status", "--porcelain", cwd=core))
    if (actual != spec["commit"] or dirty) and not args.allow_local_core:
        raise ValueError("The core checkout does not exactly match desktop/core.json")
    if args.allow_local_core and os.environ.get("GITHUB_ACTIONS"):
        raise ValueError("CI cannot use an unpinned working tree")
    if not (core / "component/desktop").is_dir():
        raise ValueError("The core does not implement the desktop-managed API")
    BINARIES.mkdir(parents=True, exist_ok=True)
    (RESOURCES / "licenses").mkdir(parents=True, exist_ok=True)
    core_version = (core / "PROJECT_VERSION").read_text().strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", core_version):
        raise ValueError("Invalid core version")
    source_time = run("git", "show", "-s", "--format=%cI", "HEAD", cwd=core)
    flags = f"-s -w -buildid= -X github.com/metacubex/mihomo/constant.Version=mihomo-optimized-v{core_version}-desktop -X github.com/metacubex/mihomo/constant.BuildTime={source_time}"
    environment = {**os.environ, "CGO_ENABLED": "0", "GOOS": "windows", "GOARCH": "amd64", "GOAMD64": "v1", "GOTOOLCHAIN": "local", "GOFLAGS": "-mod=readonly"}
    binary = BINARIES / f"mihomo-core-{TARGET}.exe"
    subprocess.run(["go", "build", "-tags", "with_gvisor", "-trimpath", "-ldflags", flags, "-o", str(binary), "."], cwd=core, env=environment, check=True)
    ensure_pe(binary)
    metadata = run("go", "version", "-m", str(binary), env=environment)
    for expected in ["GOOS=windows", "GOARCH=amd64", "GOAMD64=v1", "CGO_ENABLED=0", "-tags=with_gvisor"]:
        if expected not in metadata:
            raise ValueError(f"Missing core build property: {expected}")
    if args.service:
        service = args.service.resolve()
    else:
        subprocess.run(["cargo", "build", "--locked", "--release", "--target", TARGET, "--no-default-features", "--features", "service", "--bin", "mihomo-desktop-service"], cwd=ROOT / "src-tauri", check=True)
        service = ROOT / "src-tauri/target" / TARGET / "release/mihomo-desktop-service.exe"
    ensure_pe(service)
    shutil.copy2(service, BINARIES / f"mihomo-desktop-service-{TARGET}.exe")
    cli_version = download_cli()
    shutil.copy2(core / "LICENSE", RESOURCES / "licenses/Mihomo-core-GPL3.txt")
    shutil.copy2(ROOT / "LICENSE", RESOURCES / "licenses/Dashboard-MIT.txt")
    for path in (ROOT / "public/licenses").glob("*"):
        shutil.copy2(path, RESOURCES / "licenses" / path.name)
    info = {
        "desktopVersion": application["version"],
        "desktopFramework": "WPF",
        "uiVersion": json.loads((ROOT / "package.json").read_text())["version"],
        "coreVersion": core_version, "coreCommit": actual, "coreDirty": dirty,
        "uiCommit": run("git", "rev-parse", "HEAD"),
        "repository": spec["repository"], "target": TARGET, "goToolchain": run("go", "version"),
        "verifierVersion": cli_version,
        "companions": {path.name: sha256(path) for path in sorted(BINARIES.glob("*.exe"))},
    }
    (RESOURCES / "build-info.json").write_text(json.dumps(info, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(info, indent=2))


if __name__ == "__main__":
    main()
