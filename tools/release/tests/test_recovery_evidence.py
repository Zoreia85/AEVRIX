from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "ava-readiness.py"
spec = importlib.util.spec_from_file_location("ava_readiness_recovery", MODULE_PATH)
assert spec and spec.loader
ava = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ava)


class RecoveryEvidenceTests(unittest.TestCase):
    def _write(self, payload: dict) -> Path:
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as temp:
            json.dump(payload, temp)
            path = Path(temp.name)
        self.addCleanup(path.unlink, missing_ok=True)
        return path

    def test_recovery_requires_exact_engine_hash_binding(self):
        payload = {
            "pass": True,
            "engineHostSha256": "a" * 64,
            "firstPingPassed": True,
            "crashObserved": True,
            "failClosedAfterCrash": True,
            "restartPassed": True,
            "secondPingPassed": True,
            "cleanupVerified": True,
            "failures": [],
        }
        result = ava.parse_recovery(self._write(payload), {"b" * 64})
        self.assertEqual("FAIL", result["status"])
        self.assertFalse(result["engineHashBoundToPublishedCandidate"])

    def test_recovery_requires_fail_closed_post_crash(self):
        digest = "c" * 64
        payload = {
            "pass": True,
            "engineHostSha256": digest,
            "firstPingPassed": True,
            "crashObserved": True,
            "failClosedAfterCrash": False,
            "restartPassed": True,
            "secondPingPassed": True,
            "cleanupVerified": True,
            "failures": [],
        }
        result = ava.parse_recovery(self._write(payload), {digest})
        self.assertEqual("FAIL", result["status"])

    def test_complete_recovery_passes(self):
        digest = "d" * 64
        payload = {
            "pass": True,
            "engineHostSha256": digest,
            "firstProcessId": 100,
            "secondProcessId": 200,
            "firstPingPassed": True,
            "crashObserved": True,
            "failClosedAfterCrash": True,
            "restartPassed": True,
            "secondPingPassed": True,
            "cleanupVerified": True,
            "failures": [],
        }
        result = ava.parse_recovery(self._write(payload), {digest})
        self.assertEqual("PASS", result["status"])
        self.assertTrue(result["restartPassed"])
        self.assertTrue(result["cleanupVerified"])


if __name__ == "__main__":
    unittest.main()
