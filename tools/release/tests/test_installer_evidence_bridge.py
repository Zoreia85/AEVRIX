from __future__ import annotations

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "installer-evidence-to-release-gates.py"
SPEC = importlib.util.spec_from_file_location("installer_evidence_bridge", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(MODULE)

HEX_A = "a" * 64
HEX_B = "b" * 64
HEX_C = "c" * 64
HEX_D = "d" * 64
HEX_E = "e" * 64
COMMIT = "1" * 40


def lifecycle_payload() -> dict:
    return {
        "schemaVersion": 1,
        "oldVersion": "0.0.1",
        "newVersion": "0.0.2",
        "phaseExitCodes": {
            "interruptedInstall": -1,
            "recoveryInstall": 0,
            "repair": 0,
            "upgrade": 0,
            "downgradeAttempt": 1638,
            "uninstall": 0,
        },
        "interruption": {
            "observed": True,
            "partialSurfacePresent": True,
            "recoverySucceeded": True,
        },
        "installedExecutableHashes": {
            "desktopSha256": HEX_C,
            "engineHostSha256": HEX_D,
        },
        "residueVerdict": "PASS_NO_PRODUCT_OWNED_RESIDUE",
        "userDataPreservation": "PASS",
    }


def first_run_payload() -> dict:
    return {
        "schemaVersion": 2,
        "candidateSha": COMMIT,
        "productVersion": "0.0.2",
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


def candidate_payload(lifecycle_hash: str, *, first_run_hash: str | None = None) -> dict:
    payload = {
        "schemaVersion": 7,
        "candidateSha": COMMIT,
        "canonicalPromotion": "BOT_AUTHORED_EXACT_CANDIDATE",
        "aevrixAuthenticodeGate": "BLOCKED_UNSIGNED_CANDIDATE",
        "oldVersion": "0.0.1",
        "newVersion": "0.0.2",
        "installerHashes": {"oldSha256": HEX_A, "newSha256": HEX_B},
        "lifecycleEvidenceSha256": lifecycle_hash,
        "defenderEvidenceFile": "defender-evidence.json",
        "defenderEvidenceSha256": HEX_E,
    }
    if first_run_hash is not None:
        payload["firstRunTermsEvidenceFile"] = "first-run-terms-evidence.json"
        payload["firstRunTermsEvidenceSha256"] = first_run_hash
        payload["firstRunTerms"] = first_run_payload()
    return payload


def defender_payload() -> dict:
    return {
        "schemaVersion": 1,
        "candidateSha": COMMIT,
        "status": "PASS",
        "reason": "clean",
        "engineVersion": "1.1",
        "productVersion": "4.18",
        "signatureVersion": "1.2.3",
        "signatureLastUpdated": "2026-08-17T12:00:00Z",
        "antivirusEnabled": True,
        "amServiceEnabled": True,
        "realTimeProtectionEnabled": True,
        "artifactHashesBeforeScan": {"old.exe": HEX_A, "new.exe": HEX_B},
        "artifactHashesAfterScan": {"old.exe": HEX_A, "new.exe": HEX_B},
        "matchingDetectionCount": 0,
        "detections": [],
    }


class InstallerEvidenceBridgeTests(unittest.TestCase):
    def test_valid_exact_lifecycle_remains_valid_without_synthetic_first_run(self) -> None:
        lifecycle = lifecycle_payload()
        details = MODULE.validate_installer_evidence(candidate_payload(HEX_E), lifecycle, COMMIT)
        self.assertEqual(details["residueVerdict"], "PASS_NO_PRODUCT_OWNED_RESIDUE")
        self.assertEqual(details["phaseExitCodes"]["downgradeAttempt"], 1638)

    def test_candidate_sha_mismatch_is_rejected(self) -> None:
        candidate = candidate_payload(HEX_E)
        candidate["candidateSha"] = "2" * 40
        with self.assertRaisesRegex(ValueError, "candidateSha"):
            MODULE.validate_installer_evidence(candidate, lifecycle_payload(), COMMIT)

    def test_downgrade_must_be_rejected(self) -> None:
        lifecycle = lifecycle_payload()
        lifecycle["phaseExitCodes"]["downgradeAttempt"] = 0
        with self.assertRaisesRegex(ValueError, "1638"):
            MODULE.validate_installer_evidence(candidate_payload(HEX_E), lifecycle, COMMIT)

    def test_residue_failure_is_rejected(self) -> None:
        lifecycle = lifecycle_payload()
        lifecycle["residueVerdict"] = "FAIL"
        with self.assertRaisesRegex(ValueError, "residue"):
            MODULE.validate_installer_evidence(candidate_payload(HEX_E), lifecycle, COMMIT)

    def test_lifecycle_hash_binding(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            lifecycle_path = root / "lifecycle.json"
            lifecycle_path.write_text(json.dumps(lifecycle_payload()), encoding="utf-8")
            digest = hashlib.sha256(lifecycle_path.read_bytes()).hexdigest()
            candidate = candidate_payload(digest)
            self.assertEqual(candidate["lifecycleEvidenceSha256"], MODULE.sha256_file(lifecycle_path))

    def test_exact_first_run_hash_and_semantics_are_accepted(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "first-run-terms-evidence.json"
            path.write_text(json.dumps(first_run_payload()), encoding="utf-8")
            digest = MODULE.sha256_file(path)
            actual_hash, details = MODULE.validate_first_run_bundle(candidate_payload(HEX_E, first_run_hash=digest), path, COMMIT)
            self.assertEqual(digest, actual_hash)
            self.assertEqual(COMMIT, details["candidateSha"])
            self.assertTrue(details["commandCenterTransitionObserved"])
            self.assertTrue(details["preAcceptanceNavigationAbsent"])
            self.assertTrue(details["declineExitedWithoutAcceptance"])

    def test_first_run_hash_mismatch_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "first-run-terms-evidence.json"
            path.write_text(json.dumps(first_run_payload()), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "SHA-256"):
                MODULE.validate_first_run_bundle(candidate_payload(HEX_E, first_run_hash=HEX_A), path, COMMIT)

    def test_first_run_embedded_candidate_mismatch_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "first-run-terms-evidence.json"
            first_run = first_run_payload()
            path.write_text(json.dumps(first_run), encoding="utf-8")
            digest = MODULE.sha256_file(path)
            candidate = candidate_payload(HEX_E, first_run_hash=digest)
            candidate["firstRunTerms"]["preAcceptanceNavigationAbsent"] = False
            with self.assertRaisesRegex(ValueError, "embedded firstRunTerms mismatch"):
                MODULE.validate_first_run_bundle(candidate, path, COMMIT)

    def test_first_run_candidate_sha_mismatch_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "first-run-terms-evidence.json"
            first_run = first_run_payload()
            first_run["candidateSha"] = "2" * 40
            path.write_text(json.dumps(first_run), encoding="utf-8")
            digest = MODULE.sha256_file(path)
            candidate = candidate_payload(HEX_E, first_run_hash=digest)
            candidate["firstRunTerms"] = first_run
            with self.assertRaisesRegex(ValueError, "candidateSha"):
                MODULE.validate_first_run_bundle(candidate, path, COMMIT)

    def test_valid_defender_pass_is_only_distribution_partial(self) -> None:
        status, details = MODULE.validate_defender_evidence(candidate_payload(HEX_E), defender_payload(), COMMIT)
        self.assertEqual(status, "PASS")
        self.assertEqual(details["matchingDetectionCount"], 0)

    def test_defender_post_scan_hash_mismatch_is_rejected(self) -> None:
        defender = defender_payload()
        defender["artifactHashesAfterScan"]["new.exe"] = HEX_C
        with self.assertRaisesRegex(ValueError, "post-scan hashes"):
            MODULE.validate_defender_evidence(candidate_payload(HEX_E), defender, COMMIT)

    def test_defender_pass_with_detection_is_rejected(self) -> None:
        defender = defender_payload()
        defender["matchingDetectionCount"] = 1
        defender["detections"] = [{"threatId": "42", "resources": ["new.exe"]}]
        with self.assertRaisesRegex(ValueError, "detections"):
            MODULE.validate_defender_evidence(candidate_payload(HEX_E), defender, COMMIT)

    def test_defender_pass_requires_active_antivirus_service(self) -> None:
        defender = defender_payload()
        defender["antivirusEnabled"] = False
        with self.assertRaisesRegex(ValueError, "antivirusEnabled"):
            MODULE.validate_defender_evidence(candidate_payload(HEX_E), defender, COMMIT)


if __name__ == "__main__":
    unittest.main()
