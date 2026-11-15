#!/usr/bin/env python3
"""Render only the packaged desktop version's entry from its changelog."""
import argparse
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = 'chummumm/mihomo-optimized'
VERSION = re.compile(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)')


def version_entry(changelog, version):
    if not isinstance(version, str) or not VERSION.fullmatch(version):
        raise ValueError('Invalid desktop version')
    headings = list(re.finditer(r'^## (.+)$', changelog, re.MULTILINE))
    entries = {}
    for index, heading in enumerate(headings):
        key = heading.group(1).strip()
        if not VERSION.fullmatch(key):
            raise ValueError(f'Invalid changelog version heading: {key}')
        if key in entries:
            raise ValueError(f'Duplicate desktop changelog version: {key}')
        end = headings[index + 1].start() if index + 1 < len(headings) else len(changelog)
        entries[key] = changelog[heading.end():end].strip()
    if not entries.get(version):
        raise ValueError(f'Missing desktop changelog entry for {version}')
    return entries[version]


def render_notes(info, changelog):
    if info.get('repository') != REPOSITORY:
        raise ValueError('Unexpected desktop release repository')
    for field in ('desktopVersion', 'coreVersion', 'uiVersion'):
        value = info.get(field)
        if not isinstance(value, str) or not VERSION.fullmatch(value):
            raise ValueError(f'Invalid build-info field: {field}')
    commit = info.get('uiCommit')
    if not isinstance(commit, str) or not re.fullmatch(r'[a-f0-9]{40}', commit):
        raise ValueError('Invalid desktop source commit')
    version = info['desktopVersion']
    changes = version_entry(changelog, version)
    base = f'https://github.com/{REPOSITORY}'
    downloads = f'{base}/releases/download/mihomo-desktop-v{version}'
    guide = f'{base}/blob/{commit}/desktop/README.zh-CN.md'
    return f'''随包内核 **{info['coreVersion']}** · 网页面板 **{info['uiVersion']}** · Windows x64

## 本次更新

{changes}

## 下载

- [安装版]({downloads}/mihomo-desktop-{version}-windows-x64-setup.exe)：TUN 用户请选择安装版。
- [便携版]({downloads}/mihomo-desktop-{version}-windows-x64-portable.zip)：退出旧程序后，使用完整新版目录。

升级前从托盘退出旧程序，使用完整安装包覆盖升级；用户配置保留。

[安装、配置与验证指南]({guide})
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build-info', type=Path, default=ROOT / 'artifacts/desktop/build-info.json')
    parser.add_argument('--changelog', type=Path, default=ROOT / 'desktop/CHANGELOG.md')
    parser.add_argument('--check', action='store_true', help='Check the source desktop version before building')
    args = parser.parse_args()
    changelog = args.changelog.read_text(encoding='utf-8')
    if args.check:
        version = json.loads((ROOT / 'desktop/app.json').read_text(encoding='utf-8'))['version']
        version_entry(changelog, version)
        print(f'Desktop {version} has one release note entry.')
    else:
        info = json.loads(args.build_info.read_text(encoding='utf-8'))
        print(render_notes(info, changelog), end='')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError) as error:
        sys.exit(f'Desktop release notes: {error}')
