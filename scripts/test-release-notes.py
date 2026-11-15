#!/usr/bin/env python3
"""Check version selection, publication identity and automatic-sync notes."""

import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("release_notes", ROOT / "scripts/release-notes.py")
notes = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(notes)
SHA = "a" * 40
DOCUMENT = "# History\n\n## 1.0.20\n\n### 本次更新\n\n- Current only.\n\n## 1.0.19\n\n- Old only.\n"


class ReleaseNotesTests(unittest.TestCase):
    def test_only_exact_version_is_published_with_pinned_links(self):
        body = notes.render_notes(DOCUMENT, "1.0.20", SHA)
        self.assertIn("Current only.", body)
        self.assertNotIn("Old only.", body)
        self.assertIn("/blob/mihomo-optimized-v1.0.20/docs/releases.md", body)
        self.assertIn("/tree/" + SHA, body)
        self.assertIn("/compare/mihomo-optimized-v1.0.19...mihomo-optimized-v1.0.20", body)
        self.assertIn("/download/mihomo-optimized-v1.0.20/SHA256SUMS", body)
        self.assertNotIn("/main/", body)

    def test_missing_duplicate_empty_and_misordered_sections_fail(self):
        with self.assertRaisesRegex(ValueError, "Missing release"):
            notes.render_notes(DOCUMENT, "1.0.21", SHA)
        for document in [
            DOCUMENT + "\n## 1.0.20\n\n- Duplicate.\n",
            "## 1.0.20\n\n## 1.0.19\n\n- Old.\n",
            "## 1.0.19\n\n- Old.\n\n## 1.0.20\n\n- New.\n",
            "## 本次更新：1.0.20\n\n- Wrong heading.\n",
        ]:
            with self.subTest(document=document), self.assertRaises(ValueError):
                notes.parse_changelog(document)

    def test_mismatched_tag_build_and_invalid_version_fail(self):
        for kwargs in [
            {"tag": "mihomo-optimized-v1.0.19"},
            {"build_version": "mihomo-optimized-v1.0.19"},
            {"tag": "mihomo-desktop-v1.0.20"},
            {"tag": ""},
            {"build_version": ""},
        ]:
            with self.subTest(kwargs=kwargs), self.assertRaisesRegex(ValueError, "match PROJECT_VERSION"):
                notes.render_notes(DOCUMENT, "1.0.20", SHA, **kwargs)
        for version in ["01.0.20", "1.0.20-rc1", "18446744073709551616.0.0"]:
            with self.subTest(version=version), self.assertRaises(ValueError):
                notes.version_key(version)

    def test_historical_section_does_not_inherit_newer_notes(self):
        body = notes.render_notes(DOCUMENT, "1.0.19", SHA)
        self.assertIn("Old only.", body)
        self.assertNotIn("Current only.", body)
        self.assertNotIn("/compare/", body)

    def test_verified_upstream_record_is_new_and_precise(self):
        updated = notes.record_upstream(DOCUMENT, "1.0.21", "v1.19.33", SHA)
        body = notes.render_notes(updated, "1.0.21", SHA)
        self.assertIn("releases/tag/v1.19.33", body)
        self.assertIn("mihomo/commit/" + SHA, body)
        self.assertNotIn("Current only.", body)
        with self.assertRaises(ValueError):
            notes.record_upstream(updated, "1.0.21", "v1.19.33", SHA)

    def test_repository_current_version_is_the_newest_record(self):
        document = (ROOT / "docs/changelog.md").read_text(encoding="utf-8")
        version = (ROOT / "PROJECT_VERSION").read_text().strip()
        self.assertEqual(next(iter(notes.parse_changelog(document))), version)
        notes.render_notes(document, version, SHA)


if __name__ == "__main__":
    unittest.main()
