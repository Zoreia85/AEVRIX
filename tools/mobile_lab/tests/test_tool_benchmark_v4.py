import hashlib
import tempfile
import unittest

from tools.mobile_lab.integration_benchmark import BenchmarkCase, BenchmarkEvaluator, FindingKey, ToolCaseResult
from tools.mobile_lab.tool_invocation import (
    AdbObservationPlans,
    AndroguardPlans,
    ApkAnalyzerPlans,
    ApktoolPlans,
    AuthorizedArtifact,
    InvocationPolicyError,
    JadxPlans,
)

SHA = "a" * 64


def apk(authorized=True):
    return AuthorizedArtifact("/evidence/sample.apk", SHA, authorized, "apk")


class InvocationPlanTests(unittest.TestCase):
    def test_artifact_requires_explicit_authorization(self):
        with self.assertRaises(PermissionError):
            apk(False).validate()

    def test_artifact_requires_valid_sha256(self):
        with self.assertRaises(ValueError):
            AuthorizedArtifact("/evidence/a.apk", "not-a-sha", True, "apk").validate()

    def test_apkanalyzer_read_only_plans_match_official_cli_shapes(self):
        artifact = apk()
        plans = [
            ApkAnalyzerPlans.summary(artifact),
            ApkAnalyzerPlans.manifest(artifact),
            ApkAnalyzerPlans.permissions(artifact),
            ApkAnalyzerPlans.dex_list(artifact),
            ApkAnalyzerPlans.files_list(artifact),
        ]
        self.assertEqual(("apkanalyzer", "apk", "summary", artifact.path), plans[0].argv)
        self.assertEqual(("apkanalyzer", "manifest", "print", artifact.path), plans[1].argv)
        self.assertEqual(("apkanalyzer", "manifest", "permissions", artifact.path), plans[2].argv)
        self.assertEqual(("apkanalyzer", "dex", "list", artifact.path), plans[3].argv)
        self.assertEqual(("apkanalyzer", "files", "list", artifact.path), plans[4].argv)
        self.assertTrue(all(not p.target_mutation and p.network_mode == "offline" for p in plans))

    def test_jadx_output_is_confined_to_workspace(self):
        with tempfile.TemporaryDirectory() as root:
            plan = JadxPlans.decompile(apk(), root)
            self.assertTrue(plan.output_path.startswith(root))
            self.assertEqual("jadx", plan.argv[0])
            self.assertFalse(plan.target_mutation)

    def test_apktool_rejects_non_apk(self):
        artifact = AuthorizedArtifact("/evidence/app.aab", SHA, True, "aab")
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(InvocationPolicyError):
                ApktoolPlans.decode(artifact, root)

    def test_androguard_plans_are_offline_and_non_mutating(self):
        manifest = AndroguardPlans.manifest(apk())
        signatures = AndroguardPlans.signatures(apk())
        self.assertEqual(("androguard", "axml", apk().path), manifest.argv)
        self.assertEqual(("androguard", "sign", apk().path), signatures.argv)
        self.assertFalse(manifest.target_mutation)
        self.assertFalse(signatures.target_mutation)
        self.assertEqual("offline", manifest.network_mode)

    def test_adb_serial_injection_is_rejected(self):
        with self.assertRaises(InvocationPolicyError):
            AdbObservationPlans.logcat_snapshot("emulator-5554;rm")

    def test_adb_observation_plans_are_read_only(self):
        plans = [
            AdbObservationPlans.devices(),
            AdbObservationPlans.getprop("emulator-5554"),
            AdbObservationPlans.logcat_snapshot("emulator-5554"),
            AdbObservationPlans.screenshot("emulator-5554"),
            AdbObservationPlans.activity_state("emulator-5554"),
        ]
        self.assertTrue(all(not p.target_mutation for p in plans))
        self.assertTrue(all(p.network_mode == "offline" for p in plans))
        self.assertEqual(("adb", "-s", "emulator-5554", "exec-out", "screencap", "-p"), plans[3].argv)

    def test_command_hash_is_deterministic(self):
        first = ApkAnalyzerPlans.summary(apk()).command_sha256
        second = ApkAnalyzerPlans.summary(apk()).command_sha256
        self.assertEqual(first, second)
        expected = hashlib.sha256(b"apkanalyzer\0apk\0summary\0/evidence/sample.apk").hexdigest()
        self.assertEqual(expected, first)


class BenchmarkTests(unittest.TestCase):
    A = FindingKey("manifest", "permission", "camera")
    B = FindingKey("manifest", "permission", "internet")
    C = FindingKey("dex", "class", "com.example.Main")
    D = FindingKey("resource", "layout", "activity_main")

    def test_known_truth_calculates_precision_recall_f1(self):
        cases = [BenchmarkCase("c1", SHA, frozenset({self.A, self.B, self.C}))]
        results = [ToolCaseResult("jadx", "c1", "SUCCESS", frozenset({self.A, self.C, self.D}), 120.0)]
        summary = BenchmarkEvaluator.evaluate(cases, results).summaries[0]
        self.assertEqual("MEASURED", summary.accuracy_status)
        self.assertEqual((2, 1, 1), (summary.true_positives, summary.false_positives, summary.false_negatives))
        self.assertAlmostEqual(2 / 3, summary.recall)
        self.assertAlmostEqual(2 / 3, summary.precision)
        self.assertAlmostEqual(2 / 3, summary.f1)

    def test_unknown_truth_never_fabricates_accuracy(self):
        cases = [BenchmarkCase("c1", SHA, None)]
        results = [ToolCaseResult("jadx", "c1", "SUCCESS", frozenset({self.A}), 50.0)]
        summary = BenchmarkEvaluator.evaluate(cases, results).summaries[0]
        self.assertEqual("UNMEASURED", summary.accuracy_status)
        self.assertIsNone(summary.true_positives)
        self.assertIsNone(summary.precision)
        self.assertIsNone(summary.recall)
        self.assertIsNone(summary.f1)

    def test_reliability_counts_failures_without_converting_them_to_accuracy(self):
        cases = [BenchmarkCase("c1", SHA, frozenset({self.A})), BenchmarkCase("c2", "b" * 64, None)]
        results = [
            ToolCaseResult("apktool", "c1", "SUCCESS", frozenset({self.A}), 10.0),
            ToolCaseResult("apktool", "c2", "TIMEOUT", frozenset(), 1000.0),
        ]
        summary = BenchmarkEvaluator.evaluate(cases, results).summaries[0]
        self.assertEqual((2, 1, 0.5), (summary.attempts, summary.successes, summary.reliability))
        self.assertEqual("MEASURED", summary.accuracy_status)
        self.assertEqual(10.0, summary.median_duration_ms)

    def test_disagreement_is_explicit_evidence(self):
        cases = [BenchmarkCase("c1", SHA, None)]
        results = [
            ToolCaseResult("jadx", "c1", "SUCCESS", frozenset({self.A}), 10.0),
            ToolCaseResult("androguard", "c1", "SUCCESS", frozenset({self.A, self.B}), 20.0),
        ]
        report = BenchmarkEvaluator.evaluate(cases, results)
        self.assertEqual(1, len(report.disagreement_cases))
        self.assertEqual("c1", report.disagreement_cases[0]["case_id"])

    def test_unique_signal_is_not_treated_as_accuracy(self):
        cases = [BenchmarkCase("c1", SHA, None)]
        results = [
            ToolCaseResult("jadx", "c1", "SUCCESS", frozenset({self.A, self.C}), 10.0),
            ToolCaseResult("apktool", "c1", "SUCCESS", frozenset({self.A}), 20.0),
        ]
        report = BenchmarkEvaluator.evaluate(cases, results)
        jadx = next(s for s in report.summaries if s.capability_id == "jadx")
        self.assertEqual(1, jadx.unique_observed_signals)
        self.assertEqual("UNMEASURED", jadx.accuracy_status)

    def test_report_hash_is_deterministic(self):
        cases = [BenchmarkCase("c1", SHA, None)]
        results = [ToolCaseResult("jadx", "c1", "SUCCESS", frozenset({self.A}), 10.0)]
        first = BenchmarkEvaluator.evaluate(cases, results).sha256
        second = BenchmarkEvaluator.evaluate(cases, results).sha256
        self.assertEqual(first, second)
        self.assertEqual(64, len(first))

    def test_unknown_case_result_is_rejected(self):
        with self.assertRaises(ValueError):
            BenchmarkEvaluator.evaluate([], [ToolCaseResult("jadx", "missing", "SUCCESS", frozenset(), 1.0)])

    def test_duplicate_case_ids_are_rejected(self):
        cases = [BenchmarkCase("c1", SHA, None), BenchmarkCase("c1", "b" * 64, None)]
        with self.assertRaises(ValueError):
            BenchmarkEvaluator.evaluate(cases, [])


if __name__ == "__main__":
    unittest.main()
