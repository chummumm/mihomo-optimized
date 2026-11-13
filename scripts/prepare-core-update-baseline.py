#!/usr/bin/env python3
"""Build an explicit protocol-capable CI baseline for real official core updates.

The installed source binary is a controlled fixture, not a published old release.
The update target, checksums, download source and replacement path remain the
normal production ones. Only the fixture's compiled version is deliberately low.
"""
import argparse
import json
from pathlib import Path
import re
import runpy


ROOT = Path(__file__).resolve().parents[1]
VERSION = "mihomo-optimized-v0.0.0-desktop"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--core-dir", type=Path, required=True)
    args = parser.parse_args()
    core = args.core_dir.resolve()
    helpers = runpy.run_path(str(ROOT / "scripts/prepare-desktop.py"))
    run, sha256 = helpers["run"], helpers["sha256"]
    spec = json.loads((ROOT / "desktop/core.json").read_text())
    info = json.loads((ROOT / "src-tauri/resources/build-info.json").read_text())
    source = run("git", "rev-parse", "HEAD", cwd=core)
    dirty = bool(run("git", "status", "--porcelain", cwd=core))
    if (spec.get("repository") != "chummumm/mihomo-optimized"
            or not re.fullmatch(r"[0-9a-f]{40}", source)
            or source != spec.get("commit") or dirty
            or info.get("coreCommit") != source or info.get("coreDirty") is not False):
        raise ValueError("The CI baseline must use the exact clean companion-core source")
    output = ROOT / "artifacts/core-update-fixture"
    output.mkdir(parents=True, exist_ok=True)
    binary = output / "baseline-core.exe"
    source_time = run("git", "show", "-s", "--format=%cI", "HEAD", cwd=core)
    build_flags = helpers["build_core"](core, binary, VERSION, source_time)
    reported = run(str(binary), "-v", cwd=output)
    if not re.search(r"\b" + re.escape(VERSION) + r"\b", reported):
        raise ValueError("The compiled CI baseline did not report its fixture version")
    record = {
        "kind": "source-built-managed-discovery-baseline",
        "source": source, "version": VERSION, "sha256": sha256(binary),
        "coreDirty": False, "goToolchain": run("go", "version"),
        "buildFlags": build_flags, "sourceTime": source_time,
    }
    (output / "build-info.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(record, indent=2))


if __name__ == "__main__":
    main()
