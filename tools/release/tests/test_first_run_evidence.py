import importlib.util
from pathlib import Path
import unittest

MODULE_PATH = Path(__file__).resolve().parents[1] / "first_run_evidence.py"
SPEC = importlib.util.spec_from_file_location("first_run_evidence", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class FirstRunEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.commit = "1" * 40
        self.version = "0.0.2"
        self.valid = {
            "schemaVersion": 2,
            "candidateSha": self.commit,
            "productVersion": self.version,
            "termsRevision": "preview-authorized-use-v1",
            "installExitCode": 0,
            "uninstallExitCode": 0,
            "presentationObserved": True,
            "presentedAtUtc": "2026-08-17T12:00:00+00:00",
            "preAcceptanceNavigationAbsent": True,
            "declineExitedWithoutAcceptance": True,
            "initialAcceptDisabled": True,
            "explicitConfirmationRequired": True,
            "acceptancePersisted": True,
            "acceptedAtUtc": "2026-08-17T12:00:05+00:00",
            "commandCenterTransitionObserved": True,
            "secondLaunchSkippedFirstRun": True,
            "acceptanceSurvivedUninstall": True,
        }

    def test_valid_exact_evidence_passes_validation(self):
        details = MODULE.validate_first_run_evidence(self.valid, self.commit, self.version)
        self.assertEqual(self.commit, details["candidateSha"])
        self.assertTrue(details["commandCenterTransitionObserved"])

    def test_candidate_mismatch_is_rejected(self):
        value = dict(self.valid, candidateSha="2" * 40)
        with self.assertRaisesRegex(ValueError, "candidateSha"):
            MODULE.validate_first_run_evidence(value, self.commit, self.version)

    def test_stale_terms_revision_is_rejected(self):
        value = dict(self.valid, termsRevision="preview-authorized-use-v0")
        with self.assertRaisesRegex(ValueError, "terms revision"):
            MODULE.validate_first_run_evidence(value, self.commit, self.version)

    def test_missing_physical_observation_is_rejected(self):
        value = dict(self.valid, initialAcceptDisabled=False)
        with self.assertRaisesRegex(ValueError, "initialAcceptDisabled"):
            MODULE.validate_first_run_evidence(value, self.commit, self.version)

    def test_pre_acceptance_navigation_exposure_is_rejected(self):
        value = dict(self.valid, preAcceptanceNavigationAbsent=False)
        with self.assertRaisesRegex(ValueError, "preAcceptanceNavigationAbsent"):
            MODULE.validate_first_run_evidence(value, self.commit, self.version)

    def test_decline_must_exit_without_acceptance(self):
        value = dict(self.valid, declineExitedWithoutAcceptance=False)
        with self.assertRaisesRegex(ValueError, "declineExitedWithoutAcceptance"):
            MODULE.validate_first_run_evidence(value, self.commit, self.version)

    def test_naive_timestamp_is_rejected(self):
        value = dict(self.valid, acceptedAtUtc="2026-08-17T12:00:05")
        with self.assertRaisesRegex(ValueError, "timezone"):
            MODULE.validate_first_run_evidence(value, self.commit, self.version)


if __name__ == "__main__":
    unittest.main()
