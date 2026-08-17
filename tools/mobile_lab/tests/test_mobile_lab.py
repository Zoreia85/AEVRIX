from __future__ import annotations

import hashlib
import plistlib
import tempfile
import unittest
import zipfile
from pathlib import Path

from tools.mobile_lab.artifact_intelligence import inspect_artifact
from tools.mobile_lab.scorecard import build_scorecard, ratio_metric
from tools.mobile_lab.state_graph import BehavioralStateGraph


class ArtifactIntelligenceTests(unittest.TestCase):
    def test_detects_apk_by_structure_not_extension(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "artifact.bin"
            with zipfile.ZipFile(path, "w") as zf:
                zf.writestr("AndroidManifest.xml", b"binary-xml")
                zf.writestr("classes.dex", b"dex\n035\x00")
                zf.writestr("resources.arsc", b"resources")
            report = inspect_artifact(path)
            self.assertEqual(report.format, "APK")
            self.assertEqual(report.platform, "android")
            self.assertGreaterEqual(report.confidence, 0.99)
            self.assertTrue(report.archive_safety and report.archive_safety.safe_for_inventory)

    def test_detects_ipa_and_reads_bounded_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "sample.zip"
            info = plistlib.dumps({
                "CFBundleIdentifier": "com.example.test",
                "CFBundleVersion": "42",
                "MinimumOSVersion": "17.0",
            }, fmt=plistlib.FMT_BINARY)
            with zipfile.ZipFile(path, "w") as zf:
                zf.writestr("Payload/Test.app/Info.plist", info)
                zf.writestr("Payload/Test.app/Test", b"mach-o-placeholder")
            report = inspect_artifact(path)
            self.assertEqual(report.format, "IPA")
            self.assertEqual(report.metadata["CFBundleIdentifier"], "com.example.test")

    def test_flags_zip_slip_without_extracting(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "hostile.apk"
            with zipfile.ZipFile(path, "w") as zf:
                zf.writestr("AndroidManifest.xml", b"manifest")
                zf.writestr("classes.dex", b"dex\n035\x00")
                zf.writestr("../escape.txt", b"blocked")
            report = inspect_artifact(path)
            self.assertFalse(report.archive_safety.safe_for_inventory)
            self.assertIn("../escape.txt", report.archive_safety.unsafe_paths)

    def test_sha256_is_exact(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "plain.bin"
            path.write_bytes(b"AEVRIX")
            report = inspect_artifact(path)
            self.assertEqual(report.sha256, hashlib.sha256(b"AEVRIX").hexdigest())


class StateGraphTests(unittest.TestCase):
    def test_ids_are_deterministic_and_coverage_is_not_invented(self) -> None:
        graph = BehavioralStateGraph()
        a1 = graph.add_state("Launch", attributes={"foreground": True})
        a2 = graph.add_state("Launch", attributes={"foreground": True})
        self.assertEqual(a1.state_id, a2.state_id)
        self.assertIsNone(graph.coverage_percent(None))
        self.assertEqual(graph.coverage_percent(4), 25.0)

    def test_transition_requires_observed_endpoints(self) -> None:
        graph = BehavioralStateGraph()
        state = graph.add_state("Launch")
        with self.assertRaises(KeyError):
            graph.add_transition(state.state_id, "tap", "state_missing")


class ScorecardTests(unittest.TestCase):
    def test_unknown_denominator_stays_unmeasured(self) -> None:
        metric = ratio_metric("states", 10, None)
        self.assertIsNone(metric.percent)
        self.assertEqual(metric.status, "UNMEASURED")

    def test_critical_divergence_blocks_homologation(self) -> None:
        score = build_scorecard(
            states=(10, 10), flows=(2, 2), functions=(3, 3), algorithms=(2, 2),
            numerical_results=(5, 5), reports=(1, 1), integrations=(2, 2),
            critical_divergences_open=1,
        )
        self.assertEqual(score.homologation_status, "NOT_HOMOLOGATED_CRITICAL_DIVERGENCE")

    def test_complete_zero_divergence_is_only_candidate(self) -> None:
        score = build_scorecard(
            states=(10, 10), flows=(2, 2), functions=(3, 3), algorithms=(2, 2),
            numerical_results=(5, 5), reports=(1, 1), integrations=(2, 2),
            critical_divergences_open=0,
        )
        self.assertEqual(score.homologation_status, "HOMOLOGATION_CANDIDATE")


if __name__ == "__main__":
    unittest.main()
