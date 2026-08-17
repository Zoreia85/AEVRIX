from __future__ import annotations

import tempfile
import unittest
import zipfile
from pathlib import Path

from tools.mobile_lab.admission import evaluate_mobile_artifact


class MobileArtifactAdmissionTests(unittest.TestCase):
    def make_zip(self, suffix: str, entries: dict[str, bytes]) -> Path:
        tmp = tempfile.NamedTemporaryFile(suffix=suffix, delete=False)
        tmp.close()
        path = Path(tmp.name)
        with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
            for name, payload in entries.items():
                zf.writestr(name, payload)
        self.addCleanup(path.unlink, missing_ok=True)
        return path

    def test_structural_apk_is_admitted_for_inventory_only(self) -> None:
        path = self.make_zip(
            ".apk",
            {
                "AndroidManifest.xml": b"manifest",
                "classes.dex": b"dex\n035\x00payload",
            },
        )
        result = evaluate_mobile_artifact(path)
        self.assertTrue(result.admitted)
        self.assertEqual(result.decision, "ADMIT_INVENTORY_ONLY")
        self.assertEqual(result.format, "APK")
        self.assertEqual(result.platform, "android")
        self.assertEqual(result.reasons, ())

    def test_extension_only_apk_is_blocked(self) -> None:
        tmp = tempfile.NamedTemporaryFile(suffix=".apk", delete=False)
        tmp.write(b"not-a-zip")
        tmp.close()
        path = Path(tmp.name)
        self.addCleanup(path.unlink, missing_ok=True)

        result = evaluate_mobile_artifact(path)
        self.assertFalse(result.admitted)
        self.assertEqual(result.decision, "BLOCK")
        self.assertIn("mobile_container_not_verified_as_archive", result.reasons)
        self.assertIn("structural_confidence_below_admission_threshold", result.reasons)

    def test_unsafe_archive_path_blocks_even_valid_apk_markers(self) -> None:
        path = self.make_zip(
            ".apk",
            {
                "AndroidManifest.xml": b"manifest",
                "classes.dex": b"dex\n035\x00payload",
                "../escape.txt": b"blocked",
            },
        )
        result = evaluate_mobile_artifact(path)
        self.assertFalse(result.admitted)
        self.assertIn("archive_contains_unsafe_paths", result.reasons)

    def test_structural_aab_is_admitted(self) -> None:
        path = self.make_zip(
            ".aab",
            {
                "BundleConfig.pb": b"cfg",
                "base/manifest/AndroidManifest.xml": b"manifest",
            },
        )
        result = evaluate_mobile_artifact(path)
        self.assertTrue(result.admitted)
        self.assertEqual(result.format, "AAB")

    def test_structural_ipa_is_admitted(self) -> None:
        path = self.make_zip(
            ".ipa",
            {"Payload/App.app/Info.plist": b"not-required-for-structural-marker"},
        )
        result = evaluate_mobile_artifact(path)
        self.assertTrue(result.admitted)
        self.assertEqual(result.format, "IPA")
        self.assertEqual(result.platform, "ios")

    def test_generic_zip_is_blocked(self) -> None:
        path = self.make_zip(".zip", {"readme.txt": b"hello"})
        result = evaluate_mobile_artifact(path)
        self.assertFalse(result.admitted)
        self.assertIn("unsupported_or_unclassified_mobile_format", result.reasons)
        self.assertIn("structural_confidence_below_admission_threshold", result.reasons)


if __name__ == "__main__":
    unittest.main()
