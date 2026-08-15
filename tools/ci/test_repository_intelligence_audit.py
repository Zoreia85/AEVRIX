#!/usr/bin/env python3
from __future__ import annotations

import copy
import importlib.util
import json
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("repository_intelligence_audit.py")
SPEC = importlib.util.spec_from_file_location("repository_intelligence_audit", MODULE_PATH)
assert SPEC and SPEC.loader
AUDIT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUDIT)
REGISTRY_PATH = MODULE_PATH.parents[1] / ".." / "docs" / "manifests" / "repository-intelligence.json"
REGISTRY_PATH = REGISTRY_PATH.resolve()


class RepositoryIntelligenceAuditTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.registry = json.loads(REGISTRY_PATH.read_text(encoding="utf-8"))

    def test_current_registry_passes(self) -> None:
        self.assertEqual([], AUDIT.audit_registry(copy.deepcopy(self.registry)))

    def test_discovery_seed_cannot_be_runtime_approved(self) -> None:
        data = copy.deepcopy(self.registry)
        record = next(item for item in data["repositories"] if item["repository"] == "public-apis/public-apis")
        record["runtimeApproval"] = "Approved"
        record["pinnedRevision"] = "0" * 40
        record["securityReview"] = "test"
        failures = AUDIT.audit_registry(data)
        self.assertTrue(any("discovery catalogs can never be runtime-approved" in failure for failure in failures))

    def test_noassertion_license_cannot_be_vendored(self) -> None:
        data = copy.deepcopy(self.registry)
        record = next(item for item in data["repositories"] if item["repository"] == "ripienaar/free-for-dev")
        record["integrationModes"] = ["Vendored"]
        failures = AUDIT.audit_registry(data)
        self.assertTrue(any("unverified licensing forbids vendoring/runtime approval" in failure for failure in failures))

    def test_approved_runtime_requires_pin_and_security_review(self) -> None:
        data = copy.deepcopy(self.registry)
        record = next(item for item in data["repositories"] if item["repository"] == "ollama/ollama")
        record["runtimeApproval"] = "Approved"
        record.pop("pinnedRevision", None)
        record.pop("securityReview", None)
        failures = AUDIT.audit_registry(data)
        self.assertTrue(any("Approved runtime requires pinnedRevision" in failure for failure in failures))
        self.assertTrue(any("Approved runtime requires securityReview" in failure for failure in failures))

    def test_missing_required_seed_fails(self) -> None:
        data = copy.deepcopy(self.registry)
        data["repositories"] = [
            item for item in data["repositories"] if item["repository"] != "OpenHands/OpenHands"
        ]
        failures = AUDIT.audit_registry(data)
        self.assertTrue(any("missing required repository seeds" in failure for failure in failures))

    def test_invalid_verification_timestamp_fails(self) -> None:
        data = copy.deepcopy(self.registry)
        data["verifiedAtUtc"] = "2026-08-15 17:30"
        failures = AUDIT.audit_registry(data)
        self.assertIn("verifiedAtUtc must be an ISO-8601 UTC timestamp ending in Z", failures)


if __name__ == "__main__":
    unittest.main()
