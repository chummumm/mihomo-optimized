#!/usr/bin/env python3
"""Use the shipped verifier version for the release's provenance acceptance gate."""
import hashlib
import json
import os
from pathlib import Path
import tarfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
spec = json.loads((ROOT / 'desktop/third-party.json').read_text())['githubCli']
directory = Path(os.environ['RUNNER_TEMP']) / 'mihomo-release-verifier'
directory.mkdir(exist_ok=True)
archive = directory / 'gh.tar.gz'
request = urllib.request.Request(spec['linuxUrl'], headers={'User-Agent': 'mihomo-desktop-builder'})
with urllib.request.urlopen(request, timeout=120) as response, archive.open('wb') as output:
    digest = hashlib.sha256()
    total = 0
    while block := response.read(1024 * 1024):
        total += len(block)
        if total > 150 * 1024 * 1024:
            raise ValueError('Verifier archive exceeds its size limit')
        digest.update(block)
        output.write(block)
if digest.hexdigest() != spec['linuxSha256']:
    raise ValueError('Verifier checksum mismatch')
with tarfile.open(archive) as bundle:
    member = bundle.getmember(f"gh_{spec['version']}_linux_amd64/bin/gh")
    if not member.isfile() or member.size > 200 * 1024 * 1024:
        raise ValueError('Unexpected verifier archive layout')
    with bundle.extractfile(member) as source, (directory / 'gh').open('wb') as output:
        while block := source.read(1024 * 1024):
            output.write(block)
(directory / 'gh').chmod(0o755)
with open(os.environ['GITHUB_PATH'], 'a', encoding='utf-8') as output:
    output.write(str(directory) + '\n')
print(f"Prepared GitHub CLI {spec['version']} for release verification")
