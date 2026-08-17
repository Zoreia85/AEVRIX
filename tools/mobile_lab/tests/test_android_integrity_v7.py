import unittest

from tools.mobile_lab.android_integrity import (
    ANDROID_INTEGRITY_CANDIDATES, ANDROID_INTEGRITY_TOOL_PROBES, ApkSignerPlans,
    BundletoolPlans, parse_apksigner_verify_output,
)
from tools.mobile_lab.tool_invocation import AuthorizedArtifact, InvocationPolicyError

SHA = "a" * 64


def artifact(kind):
    suffix = ".apk" if kind == "apk" else ".aab"
    return AuthorizedArtifact("/e/app" + suffix, SHA, True, kind)


class AndroidIntegrityPlanTests(unittest.TestCase):
    def test_apksigner_verify_is_read_only_offline_and_prints_certs(self):
        plan = ApkSignerPlans.verify(artifact("apk"))
        self.assertEqual(("apksigner", "verify", "--verbose", "--print-certs", "/e/app.apk"), plan.argv)
        self.assertFalse(plan.target_mutation)
        self.assertEqual("offline", plan.network_mode)

    def test_apksigner_rejects_aab(self):
        with self.assertRaises(InvocationPolicyError):
            ApkSignerPlans.verify(artifact("aab"))

    def test_bundletool_dump_targets_match_official_read_only_surface(self):
        aab = artifact("aab")
        plans = [
            BundletoolPlans.manifest(aab),
            BundletoolPlans.resources(aab),
            BundletoolPlans.config(aab),
            BundletoolPlans.runtime_enabled_sdk_config(aab),
        ]
        self.assertEqual(("bundletool", "dump", "manifest", "--bundle=/e/app.aab"), plans[0].argv)
        self.assertEqual(("bundletool", "dump", "resources", "--bundle=/e/app.aab"), plans[1].argv)
        self.assertEqual(("bundletool", "dump", "config", "--bundle=/e/app.aab"), plans[2].argv)
        self.assertEqual(("bundletool", "dump", "runtime-enabled-sdk-config", "--bundle=/e/app.aab"), plans[3].argv)
        self.assertTrue(all(not p.target_mutation and p.network_mode == "offline" for p in plans))

    def test_bundletool_rejects_apk(self):
        with self.assertRaises(InvocationPolicyError):
            BundletoolPlans.manifest(artifact("apk"))


class ApkSignerEvidenceParserTests(unittest.TestCase):
    FIXTURE = """Verifies\nVerified using v1 scheme (JAR signing): false\nVerified using v2 scheme (APK Signature Scheme v2): true\nVerified using v3 scheme (APK Signature Scheme v3): true\nSigner #1 certificate SHA-256 digest: aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa\n"""

    def test_parser_extracts_scheme_and_certificate_digest(self):
        evidence = parse_apksigner_verify_output(self.FIXTURE, True)
        self.assertTrue(evidence.overall_verified)
        self.assertEqual({"v1": False, "v2": True, "v3": True}, evidence.schemes)
        self.assertEqual(("aa" * 32,), evidence.signer_certificate_sha256)
        self.assertEqual("PARSED", evidence.parser_status)

    def test_failed_process_is_not_reported_verified(self):
        evidence = parse_apksigner_verify_output("DOES NOT VERIFY", False)
        self.assertFalse(evidence.overall_verified)
        self.assertEqual("PROCESS_FAILED", evidence.parser_status)

    def test_unknown_success_output_stays_unmeasured(self):
        evidence = parse_apksigner_verify_output("future output format", True)
        self.assertIsNone(evidence.overall_verified)
        self.assertEqual("UNMEASURED_OUTPUT_FORMAT", evidence.parser_status)


class IntegrityCandidateTests(unittest.TestCase):
    def test_candidates_are_benchmark_gated(self):
        ids = {candidate.id for candidate in ANDROID_INTEGRITY_CANDIDATES}
        self.assertEqual({"android-apksigner-verify", "android-bundletool-readonly"}, ids)
        self.assertTrue(all(candidate.benchmark_required for candidate in ANDROID_INTEGRITY_CANDIDATES))

    def test_apksigner_probe_uses_version_only(self):
        self.assertEqual(("--version",), ANDROID_INTEGRITY_TOOL_PROBES[0].version_args)


if __name__ == "__main__":
    unittest.main()
