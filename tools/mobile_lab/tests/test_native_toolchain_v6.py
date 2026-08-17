import importlib.metadata
import tempfile
import unittest

from tools.mobile_lab.native_toolchain import (
    ApkidPlans,
    AuthorizedDerivedArtifact,
    GhidraPlans,
    NATIVE_INTEGRATION_CANDIDATES,
    NATIVE_TOOL_PROBES,
    PythonDistributionProbe,
)
from tools.mobile_lab.tool_invocation import InvocationPolicyError

SHA = "a" * 64
SOURCE = "b" * 64


class DerivedArtifactTests(unittest.TestCase):
    def test_authorization_and_source_hash_are_required(self):
        with self.assertRaises(PermissionError):
            AuthorizedDerivedArtifact("/e/lib.so", SHA, SOURCE, False, "elf").validate()
        with self.assertRaises(ValueError):
            AuthorizedDerivedArtifact("/e/lib.so", SHA, "bad", True, "elf").validate()

    def test_unsupported_kind_is_rejected(self):
        with self.assertRaises(ValueError):
            AuthorizedDerivedArtifact("/e/file.txt", SHA, SOURCE, True, "text").validate()


class NativePlanTests(unittest.TestCase):
    def test_apkid_is_json_offline_and_non_mutating(self):
        plan = ApkidPlans.scan("/e/classes.dex", SHA, SOURCE)
        self.assertEqual(("apkid", "-j", "/e/classes.dex"), plan.argv)
        self.assertEqual("offline", plan.network_mode)
        self.assertFalse(plan.target_mutation)

    def test_apkid_accepts_authorized_apk_or_derived_dex_only(self):
        apk_plan = ApkidPlans.scan("/e/app.apk", SHA, SHA, kind="apk")
        self.assertEqual(("apkid", "-j", "/e/app.apk"), apk_plan.argv)
        with self.assertRaises(InvocationPolicyError):
            ApkidPlans.scan("/e/lib.so", SHA, SOURCE, kind="elf")

    def test_ghidra_headless_is_read_only_ephemeral_and_bounded(self):
        artifact = AuthorizedDerivedArtifact("/e/libnative.so", SHA, SOURCE, True, "elf")
        with tempfile.TemporaryDirectory() as root:
            plan = GhidraPlans.headless(artifact, root, timeout_seconds=120, max_cpu=2)
            self.assertEqual("analyzeHeadless", plan.argv[0])
            self.assertIn("-readOnly", plan.argv)
            self.assertIn("-deleteProject", plan.argv)
            self.assertIn("-analysisTimeoutPerFile", plan.argv)
            self.assertIn("-max-cpu", plan.argv)
            self.assertTrue(plan.output_path.startswith(root))
            self.assertFalse(plan.target_mutation)

    def test_ghidra_rejects_dex_and_unsafe_limits(self):
        dex = AuthorizedDerivedArtifact("/e/classes.dex", SHA, SOURCE, True, "dex")
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(InvocationPolicyError):
                GhidraPlans.headless(dex, root)
            elf = AuthorizedDerivedArtifact("/e/lib.so", SHA, SOURCE, True, "elf")
            with self.assertRaises(ValueError):
                GhidraPlans.headless(elf, root, timeout_seconds=1)
            with self.assertRaises(ValueError):
                GhidraPlans.headless(elf, root, max_cpu=99)


class DistributionProbeTests(unittest.TestCase):
    def test_lief_metadata_probe_does_not_import_candidate(self):
        probe = PythonDistributionProbe(version_resolver=lambda name: "1.0.0")
        result = probe.probe("lief")
        self.assertEqual("AVAILABLE", result.status)
        self.assertEqual("1.0.0", result.version)

    def test_missing_distribution_is_evidence(self):
        def missing(name):
            raise importlib.metadata.PackageNotFoundError(name)
        result = PythonDistributionProbe(version_resolver=missing).probe("lief")
        self.assertEqual("NOT_FOUND", result.status)
        self.assertIsNone(result.version)


class CandidateTests(unittest.TestCase):
    def test_native_candidates_are_all_benchmark_gated(self):
        ids = {c.id for c in NATIVE_INTEGRATION_CANDIDATES}
        self.assertEqual({"ghidra-headless", "apkid", "lief", "appium-webdriver-lab"}, ids)
        self.assertTrue(all(c.benchmark_required for c in NATIVE_INTEGRATION_CANDIDATES))

    def test_only_safe_probe_shapes_are_registered(self):
        probes = {p.id: p for p in NATIVE_TOOL_PROBES}
        self.assertEqual(("--help",), probes["apkid"].version_args)
        self.assertEqual(("--version",), probes["appium-webdriver-lab"].version_args)


if __name__ == "__main__":
    unittest.main()
