from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "ip_boundary_audit.py"
SPEC = importlib.util.spec_from_file_location("ip_boundary_audit", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class IpBoundaryAuditTests(unittest.TestCase):
    def test_blocks_strategic_source_path(self) -> None:
        reason = MODULE.strategic_source_reason(
            "services/aevrix-remote-brain/src/ProviderArbitrationEngine.cs",
            "namespace Aevrix; public sealed class ProviderArbitrationEngine {}",
        )
        self.assertEqual("strategic source path/name", reason)

    def test_blocks_innocuous_path_with_strategic_content_marker(self) -> None:
        reason = MODULE.strategic_source_reason(
            "services/aevrix-remote-brain/src/Coordinator.cs",
            "// proprietary provider arbitration policy\npublic sealed class Coordinator {}",
        )
        self.assertEqual("strategic source content marker", reason)

    def test_allows_public_contract_surface(self) -> None:
        reason = MODULE.strategic_source_reason(
            "services/aevrix-remote-brain/src/Contracts/ProviderArbitrationContract.cs",
            "public interface IProviderArbitrationContract {}",
        )
        self.assertIsNone(reason)

    def test_allows_qa_test_surface(self) -> None:
        reason = MODULE.strategic_source_reason(
            "tests/PlanningEnginePolicyTests.cs",
            "public sealed class PlanningEnginePolicyTests {}",
        )
        self.assertIsNone(reason)

    def test_ignores_non_source_documentation(self) -> None:
        reason = MODULE.strategic_source_reason(
            "docs/architecture/provider-arbitration.md",
            "provider arbitration",
        )
        self.assertIsNone(reason)


if __name__ == "__main__":
    unittest.main()
