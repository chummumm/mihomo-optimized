#!/usr/bin/env python3
"""Restore the immutable supplied UI for Windows visual comparison only."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import subprocess


def main() -> None:
    root = Path(__file__).resolve().parents[2]
    manifest_path = Path(__file__).with_name("visual-reference.json")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    base = manifest["baseCommit"]
    if not re.fullmatch(r"[0-9a-f]{40}", base):
        raise ValueError("Invalid visual reference source pin")
    patch = manifest_path.with_name(manifest["patch"])
    if hashlib.sha256(patch.read_bytes()).hexdigest() != manifest["patchSha256"]:
        raise ValueError("The supplied visual reference patch changed")
    destination = root / "artifacts" / "reference-source"
    if destination.exists():
        raise FileExistsError("Use a fresh checkout for the visual reference")
    if subprocess.run(["git", "cat-file", "-e", base + "^{commit}"], cwd=root, capture_output=True).returncode:
        subprocess.run(["git", "fetch", "--no-tags", "--depth=1", "origin", base], cwd=root, check=True)
    subprocess.run(["git", "worktree", "add", "--detach", str(destination), base], cwd=root, check=True)
    subprocess.run(["git", "apply", "--check", str(patch)], cwd=destination, check=True)
    subprocess.run(["git", "apply", str(patch)], cwd=destination, check=True)
    print(json.dumps({"reference": str(destination), "baseCommit": base, "patchSha256": manifest["patchSha256"]}))


if __name__ == "__main__":
    main()
