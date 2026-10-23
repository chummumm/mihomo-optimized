#!/usr/bin/env python3
"""Publish or resume one desktop release without moving the core latest marker."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = 'chummumm/mihomo-optimized'


def api(path):
    result = subprocess.run(['gh', 'api', f'repos/{REPOSITORY}/{path}'], capture_output=True, text=True)
    if result.returncode:
        if 'HTTP 404' in result.stderr:
            return None
        raise RuntimeError('GitHub release API failed: ' + result.stderr[:500])
    return json.loads(result.stdout)


def release_for_tag(tag):
    # REST /releases/tags only finds published releases. The CLI also queries
    # GraphQL for a draft's pending tag, allowing interrupted uploads to resume.
    result = subprocess.run(
        ['gh', 'release', 'view', tag, '--repo', REPOSITORY,
         '--json', 'isDraft,targetCommitish,tagName'],
        capture_output=True, text=True,
    )
    if result.returncode:
        error = result.stderr.strip()
        if error == 'release not found' or re.search(r'\bHTTP 404\b', error):
            return None
        raise RuntimeError('GitHub release lookup failed: ' + error[:500])
    release = json.loads(result.stdout)
    if (release.get('tagName') != tag or not isinstance(release.get('isDraft'), bool)
            or not isinstance(release.get('targetCommitish'), str)):
        raise ValueError('GitHub returned invalid release metadata')
    return {
        'draft': release['isDraft'],
        'target_commitish': release['targetCommitish'],
        'tag_name': release['tagName'],
    }


def run(*arguments):
    subprocess.run(arguments, check=True)


def tag_commit(tag):
    reference = api(f'git/ref/tags/{tag}')
    if reference is None:
        return None
    for _ in range(4):
        target = reference['object']
        if target['type'] == 'commit':
            return target['sha']
        if target['type'] != 'tag':
            break
        reference = api(f"git/tags/{target['sha']}")
    raise ValueError('Release tag does not resolve to a commit')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('operation', choices=['prepare', 'publish'])
    args = parser.parse_args()
    if os.environ.get('GITHUB_REPOSITORY') != REPOSITORY or os.environ.get('GITHUB_REF') != 'refs/heads/clash-dashboard':
        raise ValueError('Desktop releases must originate on the trusted dashboard branch')
    version = json.loads((ROOT / 'desktop/app.json').read_text())['version']
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version):
        raise ValueError('Invalid desktop version')
    source = os.environ['GITHUB_SHA']
    info = json.loads((ROOT / 'artifacts/desktop/build-info.json').read_text())
    core = json.loads((ROOT / 'desktop/core.json').read_text())
    if (info['uiCommit'] != source or info['coreDirty'] or info['coreCommit'] != core['commit']
            or info.get('desktopVersion') != version or info.get('desktopFramework') != 'WPF'):
        raise ValueError('Packaged source provenance does not match the release workflow')
    tag = f'mihomo-desktop-v{version}'
    release = release_for_tag(tag)
    if release and not release['draft']:
        if args.operation == 'publish':
            raise ValueError('Published releases are immutable; refusing to overwrite')
        publish = False
        print(f'Desktop {version} is already published. Bump its version for a new release.')
    else:
        publish = True
        existing = tag_commit(tag)
        if existing is not None and existing != source:
            raise ValueError('Existing desktop tag points to a different source commit; refusing to publish')
        if release and release.get('target_commitish') != source:
            raise ValueError('Existing draft belongs to another source commit; refusing to reuse it')
    if args.operation == 'prepare':
        with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
            output.write(f'publish={str(publish).lower()}\nversion={version}\ntag={tag}\ninstaller=artifacts/desktop/mihomo-desktop-{version}-windows-x64-setup.exe\n')
        return
    notes = Path(os.environ['RUNNER_TEMP']) / 'desktop-release-notes.md'
    with notes.open('w', encoding='utf-8') as output:
        subprocess.run([sys.executable, str(ROOT / 'scripts/desktop-release-notes.py')], stdout=output, check=True)
    title = f'Mihomo Optimized Desktop v{version}'
    if release is None:
        run('gh', 'release', 'create', tag, '--repo', REPOSITORY, '--target', source, '--draft', '--latest=false', '--title', title, '--notes-file', str(notes))
    assets = sorted(str(path) for path in (ROOT / 'artifacts/desktop').iterdir() if path.is_file())
    # Only an unpublished draft from this exact source can reach this operation.
    # A cancelled upload can safely resume on a rerun of the same workflow SHA.
    run('gh', 'release', 'upload', tag, *assets, '--repo', REPOSITORY, '--clobber')
    run('gh', 'release', 'edit', tag, '--repo', REPOSITORY, '--draft=false', '--latest=false', '--title', title, '--notes-file', str(notes))


if __name__ == '__main__':
    main()
