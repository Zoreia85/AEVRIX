from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "ava-readiness.py"
spec = importlib.util.spec_from_file_location("ava_readiness", MODULE_PATH)
assert spec and spec.loader
ava = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ava)


class ReadinessModelTests(unittest.TestCase):
    def test_valid_model(self):
        model = {
            "readinessPercent": 45,
            "releaseDecision": "NOT_HOMOLOGATED",
            "rules": {"weightsMustTotal": 100},
            "gates": [
                {"id": "a", "weight": 55, "points": 20, "status": "PARCIAL"},
                {"id": "b", "weight": 45, "points": 25, "status": "BLOQUEADO"},
            ],
        }
        self.assertEqual([], ava.validate_model(model))

    def test_100_percent_requires_all_pass(self):
        model = {
            "readinessPercent": 100,
            "releaseDecision": "NOT_HOMOLOGATED",
            "rules": {"weightsMustTotal": 100},
            "gates": [{"id": "a", "weight": 100, "points": 100, "status": "PARCIAL"}],
        }
        errors = ava.validate_model(model)
        self.assertTrue(any("100% is forbidden" in error for error in errors))

    def test_homologated_requires_100(self):
        model = {
            "readinessPercent": 99,
            "releaseDecision": "HOMOLOGATED",
            "rules": {"weightsMustTotal": 100},
            "gates": [
                {"id": "a", "weight": 99, "points": 99, "status": "PASS"},
                {"id": "b", "weight": 1, "points": 0, "status": "PASS"},
            ],
        }
        errors = ava.validate_model(model)
        self.assertTrue(any("requires readinessPercent=100" in error for error in errors))


class TrxTests(unittest.TestCase):
    def _write_trx(self, counters: str) -> Path:
        with tempfile.NamedTemporaryFile("w", suffix=".trx", delete=False, encoding="utf-8") as temp:
            temp.write(f'<TestRun><ResultSummary><Counters {counters} /></ResultSummary></TestRun>')
            return Path(temp.name)

    def test_all_passed_trx_is_pass(self):
        path = self._write_trx('total="3" executed="3" passed="3" failed="0" notExecuted="0"')
        self.addCleanup(path.unlink, missing_ok=True)
        result = ava.parse_trx(path)
        self.assertEqual("PASS", result["status"])
        self.assertEqual(3, result["passed"])

    def test_skipped_trx_is_fail(self):
        path = self._write_trx('total="3" executed="2" passed="2" failed="0" notExecuted="1"')
        self.addCleanup(path.unlink, missing_ok=True)
        result = ava.parse_trx(path)
        self.assertEqual("FAIL", result["status"])
        self.assertEqual(1, result["notExecuted"])

    def test_inconclusive_trx_is_fail(self):
        path = self._write_trx('total="2" executed="2" passed="1" failed="0" inconclusive="1"')
        self.addCleanup(path.unlink, missing_ok=True)
        result = ava.parse_trx(path)
        self.assertEqual("FAIL", result["status"])


class ExternalEvidenceTests(unittest.TestCase):
    def _write(self, payload: dict) -> Path:
        temp_dir = tempfile.TemporaryDirectory()
        self.addCleanup(temp_dir.cleanup)
        path = Path(temp_dir.name) / "external-evidence.json"
        path.write_text(json.dumps(payload), encoding="utf-8")
        return path

    def test_commit_mismatch_rejected(self):
        path = self._write({"sourceCommit": "other", "gates": {}})
        _, errors = ava.load_external_evidence(path, "exact")
        self.assertTrue(any("sourceCommit" in error for error in errors))

    def test_external_cannot_override_automatic_gate(self):
        path = self._write(
            {
                "sourceCommit": "exact",
                "gates": {
                    "core-tests": {
                        "status": "PASS",
                        "evidenceRef": "manual:test",
                        "evidenceSha256": "a" * 64,
                    }
                },
            }
        )
        accepted, errors = ava.load_external_evidence(path, "exact")
        self.assertEqual({}, accepted)
        self.assertTrue(any("cannot override" in error for error in errors))

    def test_external_pass_requires_digest_and_reference(self):
        path = self._write(
            {
                "sourceCommit": "exact",
                "gates": {"installer-lifecycle": {"status": "PASS"}},
            }
        )
        accepted, errors = ava.load_external_evidence(path, "exact")
        self.assertEqual({}, accepted)
        self.assertTrue(any("evidenceSha256" in error for error in errors))

    def test_valid_external_pass_accepted(self):
        path = self._write(
            {
                "sourceCommit": "exact",
                "gates": {
                    "installer-lifecycle": {
                        "status": "PASS",
                        "evidenceRef": "ava:installer-run-001",
                        "evidenceSha256": "b" * 64,
                        "artifactSha256": "c" * 64,
                    }
                },
            }
        )
        accepted, errors = ava.load_external_evidence(path, "exact")
        self.assertEqual([], errors)
        self.assertEqual("PASS", accepted["installer-lifecycle"]["status"])


class DesktopSurfaceTests(unittest.TestCase):
    def test_missing_desktop_surface_blocks(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            src = root / "apps" / "aevrix-windows" / "src" / "AEVRIX.Core"
            src.mkdir(parents=True)
            (src / "AEVRIX.Core.csproj").write_text("<Project />", encoding="utf-8")
            result = ava.discover_desktop_surface(root)
            self.assertEqual("BLOQUEADO", result["status"])

    def test_winui_surface_passes_physical_preflight_only(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            src = root / "apps" / "aevrix-windows" / "src" / "AEVRIX.Desktop"
            src.mkdir(parents=True)
            (src / "AEVRIX.Desktop.csproj").write_text(
                "<Project><PropertyGroup><OutputType>WinExe</OutputType><UseWinUI>true</UseWinUI></PropertyGroup></Project>",
                encoding="utf-8",
            )
            result = ava.discover_desktop_surface(root)
            self.assertEqual("PASS", result["status"])
            self.assertEqual(1, len(result["projects"]))


if __name__ == "__main__":
    unittest.main()
