#!/usr/bin/env python3
"""Check the release note selection and missing-version publication boundary."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/desktop-release-notes.py'
SPEC = importlib.util.spec_from_file_location('desktop_release_notes', SCRIPT)
NOTES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(NOTES)


class ReleaseNotesTests(unittest.TestCase):
    def manifest(self, version='0.2.10'):
        return {
            'repository': 'chummumm/mihomo-optimized',
            'desktopVersion': version,
            'coreVersion': '1.0.33',
            'uiVersion': '0.4.17',
            'uiCommit': 'a' * 40,
        }

    def test_selects_exact_version_without_neighbouring_changes(self):
        history = '# Releases\n\n## 0.2.11\n\n- Future.\n\n## 0.2.1\n\n- Earlier.\n\n## 0.2.10\n\n- Selected.\n'
        result = NOTES.render_notes(self.manifest(), history)
        self.assertIn('- Selected.', result)
        self.assertNotIn('Future.', result)
        self.assertNotIn('Earlier.', result)
        self.assertIn('/mihomo-desktop-v0.2.10/mihomo-desktop-0.2.10-windows-x64-setup.exe', result)
        self.assertIn('/blob/' + 'a' * 40 + '/desktop/README.zh-CN.md', result)
        self.assertNotIn('/blob/main/', result)
        self.assertNotIn('/blob/clash-dashboard/', result)

    def test_missing_empty_or_duplicate_entries_refuse_to_publish(self):
        for history in (
            '## 0.2.1\n\n- Earlier.\n',
            '## 0.2.10\n\n## 0.2.1\n\n- Earlier.\n',
            '## 0.2.10\n\n- First.\n\n## 0.2.10\n\n- Second.\n',
        ):
            with self.subTest(history=history), self.assertRaises(ValueError):
                NOTES.render_notes(self.manifest(), history)

    def test_build_metadata_cannot_change_the_repository_or_source_link(self):
        for field, value in (
            ('repository', 'other/repository'),
            ('uiCommit', 'clash-dashboard'),
            ('desktopVersion', '0.2.10-preview'),
            ('coreVersion', '1.0.33\nOther text'),
        ):
            info = self.manifest()
            info[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                NOTES.render_notes(info, '## 0.2.10\n\n- Selected.\n')

    def test_current_source_version_has_one_nonempty_entry(self):
        version = json.loads((ROOT / 'desktop/app.json').read_text(encoding='utf-8'))['version']
        history = (ROOT / 'desktop/CHANGELOG.md').read_text(encoding='utf-8')
        changes = NOTES.version_entry(history, version)
        self.assertIn('- ', changes)
        self.assertNotIn('\n## ', changes)

    def test_command_fails_without_printing_fallback_notes(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            metadata = directory / 'build-info.json'
            history = directory / 'CHANGELOG.md'
            metadata.write_text(json.dumps(self.manifest('9.9.9')), encoding='utf-8')
            history.write_text('## 0.2.10\n\n- Earlier.\n', encoding='utf-8')
            result = subprocess.run(
                [sys.executable, str(SCRIPT), '--build-info', str(metadata), '--changelog', str(history)],
                capture_output=True, text=True,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, '')
            self.assertIn('Missing desktop changelog entry for 9.9.9', result.stderr)


if __name__ == '__main__':
    unittest.main()
