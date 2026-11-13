"""CI baseline provenance contracts; no Windows process or release download is needed."""

from __future__ import annotations

import copy
import unittest

from core_update_smoke import (
    BASELINE_KIND, BASELINE_VERSION, validate_baseline_metadata, validate_legacy_rejection_status,
)
from windows_smoke import SmokeFailure


class BaselineProvenanceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.installed = {"coreCommit": "a" * 40, "coreDirty": False,
                          "repository": "chummumm/mihomo-optimized",
                          "goToolchain": "go version go1.26.4 windows/amd64"}
        self.pin = {"commit": "a" * 40, "repository": "chummumm/mihomo-optimized"}
        source_time = "2026-11-13T05:00:00+00:00"
        flags = ("-s -w -buildid= -X github.com/metacubex/mihomo/constant.Version=" + BASELINE_VERSION
                 + " -X github.com/metacubex/mihomo/constant.BuildTime=" + source_time)
        self.metadata = {"kind": BASELINE_KIND, "source": "a" * 40, "version": BASELINE_VERSION,
                         "sha256": "b" * 64, "coreDirty": False,
                         "goToolchain": self.installed["goToolchain"], "sourceTime": source_time,
                         "buildFlags": ["-tags", "with_gvisor", "-trimpath", "-ldflags", flags]}

    def validate(self, metadata=None, installed=None, pin=None):
        return validate_baseline_metadata(
            self.metadata if metadata is None else metadata,
            self.installed if installed is None else installed,
            self.pin if pin is None else pin)

    def test_exact_clean_source_build_has_explicit_private_provenance(self) -> None:
        result = self.validate({**self.metadata, "unreviewed": "not copied to report"})
        self.assertEqual(result, self.metadata)
        self.assertEqual(result["kind"], "source-built-managed-discovery-baseline")
        self.assertEqual(result["version"], "mihomo-optimized-v0.0.0-desktop")
        self.assertIsNot(result, self.metadata)

    def test_dirty_or_mismatched_source_cannot_claim_installed_provenance(self) -> None:
        for key, value in (("coreDirty", True), ("coreDirty", None),
                           ("source", "c" * 40), ("source", "A" * 40), ("source", "short")):
            with self.subTest(metadata_key=key):
                with self.assertRaisesRegex(SmokeFailure, "^core_fixture_baseline_source_mismatch$"):
                    self.validate({**self.metadata, key: value})
        for installed in ({**self.installed, "coreDirty": True},
                          {**self.installed, "coreCommit": "c" * 40},
                          {**self.installed, "repository": "unrelated/repository"}):
            with self.assertRaisesRegex(SmokeFailure, "^core_fixture_baseline_source_mismatch$"):
                self.validate(installed=installed)
        with self.assertRaisesRegex(SmokeFailure, "^core_fixture_baseline_source_mismatch$"):
            self.validate(pin={**self.pin, "commit": "d" * 40})

    def test_official_release_or_ambiguous_baseline_label_is_rejected(self) -> None:
        for key, value in (("kind", "published"), ("kind", None),
                           ("version", "mihomo-optimized-v1.0.28"),
                           ("version", "mihomo-optimized-v0.0.0")):
            with self.subTest(key=key, value=value):
                with self.assertRaisesRegex(SmokeFailure, "^core_fixture_baseline_source_mismatch$"):
                    self.validate({**self.metadata, key: value})

    def test_build_must_use_installed_toolchain_and_exact_production_flags(self) -> None:
        with self.assertRaisesRegex(SmokeFailure, "^core_fixture_baseline_toolchain_mismatch$"):
            self.validate({**self.metadata, "goToolchain": "go version go1.25.0 windows/amd64"})
        flags = copy.deepcopy(self.metadata["buildFlags"])
        for changed in (flags[:-1], " ".join(flags), ["-race", *flags],
                        [*flags[:-1], flags[-1].replace("0.0.0-desktop", "1.0.35")]):
            with self.subTest(flags=changed):
                with self.assertRaisesRegex(SmokeFailure, "^core_fixture_baseline_build_flags_mismatch$"):
                    self.validate({**self.metadata, "buildFlags": changed})

    def test_checksum_and_source_time_must_be_well_formed(self) -> None:
        for checksum in (None, "", "b" * 63, "B" * 64, "z" * 64):
            with self.subTest(checksum=checksum):
                with self.assertRaisesRegex(SmokeFailure, "^core_fixture_baseline_checksum_invalid$"):
                    self.validate({**self.metadata, "sha256": checksum})
        for source_time in (None, "not-a-time", "2026-11-13T05:00:00.000", "2026-99-99T05:00:00+00:00"):
            with self.subTest(source_time=source_time):
                with self.assertRaisesRegex(SmokeFailure, "^core_fixture_baseline_source_time_invalid$"):
                    self.validate({**self.metadata, "sourceTime": source_time})


class LegacyRejectionStateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.status = {
            "phase": "error", "errorCode": "core_controller_incompatible",
            "desiredRunning": True, "controller": None, "pid": None, "operation": None,
            "processState": "exited", "controlState": "unknown", "listenersReady": None,
        }

    def test_user_start_intent_is_preserved_after_confirmed_resource_exit(self) -> None:
        before = copy.deepcopy(self.status)
        validate_legacy_rejection_status(self.status)
        self.assertEqual(self.status, before)
        validate_legacy_rejection_status({**self.status, "listenersReady": False})

    def test_lost_user_intent_is_not_accepted_as_successful_cleanup(self) -> None:
        for intent in (False, None, 0, 1):
            with self.subTest(intent=intent):
                with self.assertRaisesRegex(SmokeFailure, "^core_fixture_legacy_start_intent_lost$"):
                    validate_legacy_rejection_status({**self.status, "desiredRunning": intent})

    def test_residual_or_unconfirmed_resources_and_missing_fields_fail(self) -> None:
        for key, value in (
            ("phase", "starting"), ("errorCode", "core_termination_unconfirmed"),
            ("controller", {"url": "http://127.0.0.1:45678", "secret": "fixture-only"}),
            ("pid", 42), ("operation", "stopping"), ("processState", "unknown"),
            ("controlState", "ready"), ("listenersReady", True), ("listenersReady", 0),
        ):
            with self.subTest(key=key, value=value):
                with self.assertRaisesRegex(SmokeFailure, "^core_fixture_legacy_rejection_not_terminal$"):
                    validate_legacy_rejection_status({**self.status, key: value})
        for key in self.status:
            with self.subTest(missing=key):
                status = dict(self.status)
                del status[key]
                with self.assertRaisesRegex(SmokeFailure, "^core_fixture_legacy_rejection_status_incomplete$"):
                    validate_legacy_rejection_status(status)


if __name__ == "__main__":
    unittest.main()
