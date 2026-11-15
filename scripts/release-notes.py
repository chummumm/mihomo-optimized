#!/usr/bin/env python3
"""Render exactly one version's notes, or record a verified upstream sync."""

import argparse
from pathlib import Path
import re
import subprocess


ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "chummumm/mihomo-optimized"
TAG_PREFIX = "mihomo-optimized-v"
VERSION = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
UINT64_MAX = 2**64 - 1


def version_key(version):
    if not re.fullmatch(VERSION, version):
        raise ValueError(f"Invalid release version: {version!r}")
    parts = tuple(int(part) for part in version.split("."))
    if any(part > UINT64_MAX for part in parts):
        raise ValueError("Release version exceeds uint64")
    return parts


def parse_changelog(document):
    """Require one nonempty section per canonical version, newest first."""
    sections = {}
    current = None
    lines = []

    def finish():
        if current is not None:
            body = "\n".join(lines).strip()
            if not body or not any(line.startswith("- ") for line in lines):
                raise ValueError(f"Release {current} has no change summary")
            sections[current] = body

    for line in document.splitlines():
        if line.startswith("## "):
            finish()
            version = line[3:].strip()
            key = version_key(version)
            if version in sections:
                raise ValueError(f"Duplicate release section: {version}")
            if current is not None and key >= version_key(current):
                raise ValueError("Release sections must be in descending version order")
            current = version
            lines = []
        elif current is not None:
            if line.startswith("# "):
                raise ValueError("Unexpected document heading inside release notes")
            lines.append(line)
    finish()
    if not sections:
        raise ValueError("No release sections found")
    return sections


def validate_identity(version, tag, build_version, commit):
    version_key(version)
    expected = TAG_PREFIX + version
    if tag != expected or build_version != expected:
        raise ValueError("Release tag and build version must match PROJECT_VERSION")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Source commit must be a full lowercase Git SHA")


def render_notes(document, version, commit, *, tag=None, build_version=None):
    """Historical maintenance can select any exact section; never fall back."""
    tag = TAG_PREFIX + version if tag is None else tag
    validate_identity(version, tag, tag if build_version is None else build_version, commit)
    sections = parse_changelog(document)
    if version not in sections:
        raise ValueError(f"Missing release section: {version}")
    versions = list(sections)
    index = versions.index(version)
    base = f"https://github.com/{REPOSITORY}"
    links = [
        f"[使用说明与历史记录]({base}/blob/{tag}/docs/releases.md)",
        f"[源码]({base}/tree/{commit})",
    ]
    if index + 1 < len(versions):
        previous = TAG_PREFIX + versions[index + 1]
        links.append(f"[完整变更]({base}/compare/{previous}...{tag})")
    return (sections[version] + "\n\n"
            + f"下载见下方 Assets，请使用[本版 SHA256SUMS]({base}/releases/download/{tag}/SHA256SUMS)校验。\n\n"
            + " · ".join(links) + "\n")


def record_upstream(document, project_version, upstream_tag, upstream_commit):
    """Called only after upstream-sync.sh has resolved and verified the merge."""
    sections = parse_changelog(document)
    previous = next(iter(sections))
    if version_key(project_version) <= version_key(previous):
        raise ValueError("Upstream sync needs a new project version")
    if not upstream_tag.startswith("v"):
        raise ValueError("Invalid upstream tag")
    version_key(upstream_tag[1:])
    if not re.fullmatch(r"[0-9a-f]{40}", upstream_commit):
        raise ValueError("Invalid upstream commit")
    entry = (
        f"## {project_version}\n\n### 本次更新\n\n"
        f"- 同步上游 Mihomo [{upstream_tag}]"
        f"(https://github.com/MetaCubeX/mihomo/releases/tag/{upstream_tag})。\n"
        f"- 上游基线固定为 [{upstream_commit[:12]}]"
        f"(https://github.com/MetaCubeX/mihomo/commit/{upstream_commit})；"
        "本项目改动沿用合并结果，具体差异见完整变更。\n\n"
    )
    offset = re.search(r"(?m)^## " + re.escape(previous) + r"[ \t]*$", document).start()
    result = document[:offset] + entry + document[offset:]
    parse_changelog(result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag")
    parser.add_argument("--build-version")
    parser.add_argument("--commit")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--record-upstream", metavar="TAG")
    parser.add_argument("--upstream-commit")
    args = parser.parse_args()
    path = ROOT / "docs/changelog.md"
    version = (ROOT / "PROJECT_VERSION").read_text().strip()
    document = path.read_text(encoding="utf-8")
    if args.record_upstream:
        if args.tag or args.build_version or args.commit or args.output or args.check:
            parser.error("Upstream recording cannot be combined with rendering")
        expected_tag = (ROOT / "UPSTREAM_VERSION").read_text().strip()
        expected_commit = (ROOT / "UPSTREAM_COMMIT").read_text().strip()
        if (args.record_upstream, args.upstream_commit) != (expected_tag, expected_commit):
            parser.error("Upstream identity differs from the prepared source")
        path.write_text(record_upstream(document, version, expected_tag, expected_commit), encoding="utf-8")
        return
    if args.upstream_commit:
        parser.error("--upstream-commit requires --record-upstream")
    sections = parse_changelog(document)
    if next(iter(sections)) != version:
        raise ValueError("Newest release section must match PROJECT_VERSION")
    commit = args.commit or subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    body = render_notes(document, version, commit, tag=args.tag, build_version=args.build_version)
    if args.check:
        if args.output:
            parser.error("--check cannot be combined with --output")
        print(f"Validated release notes for {version}")
    elif args.output:
        args.output.write_text(body, encoding="utf-8")
    else:
        print(body, end="")


if __name__ == "__main__":
    main()
