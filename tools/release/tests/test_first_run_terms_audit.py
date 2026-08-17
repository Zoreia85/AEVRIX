from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "audit-first-run-terms.py"
SPEC = importlib.util.spec_from_file_location("first_run_terms_audit", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(MODULE)


class FirstRunTermsAuditTests(unittest.TestCase):
    def test_method_body_extracts_balanced_method(self) -> None:
        source = "private void Test(object x) { if (true) { Foo(); } Bar(); } private void Other() {}"
        body = MODULE.method_body(source, "Test")
        self.assertIn("Foo();", body)
        self.assertIn("Bar();", body)
        self.assertNotIn("Other", body)

    def test_unconditional_route_is_not_guarded(self) -> None:
        body = 'RootNavigation.SelectedItem = HomeNavItem; ShowSection("home", "Command Center");'
        self.assertFalse(MODULE.contains_terms_guard(body))

    def test_direct_acceptance_guard_is_detected(self) -> None:
        body = "if (!firstRunAcceptanceStore.IsAccepted()) { return; } ShowSection(\"home\", \"Command Center\");"
        self.assertTrue(MODULE.contains_terms_guard(body))

    def test_current_unwired_fixture_is_blocked(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            store = root / "apps/aevrix-windows/src/AEVRIX.Core/FirstRunAcceptanceStore.cs"
            tests = root / "apps/aevrix-windows/tests/AEVRIX.Core.Tests/FirstRunAcceptanceStoreTests.cs"
            cs = root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml.cs"
            xaml = root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml"
            for path in (store, tests, cs, xaml):
                path.parent.mkdir(parents=True, exist_ok=True)
            store.write_text(
                "CurrentSchemaVersion CurrentTermsRevision return false JsonException File.Move overwrite: true",
                encoding="utf-8",
            )
            tests.write_text(
                "StaleRevision_IsRejected MissingOrMalformedAcceptance_IsFailClosed",
                encoding="utf-8",
            )
            cs.write_text(
                'private void RootNavigation_SelectionChanged(object x) { ShowSection("home", "Command Center"); }\n'
                'private void BackToHomeButton_Click(object x) { ShowSection("home", "Command Center"); }\n'
                'private void StartAnalysisButton_Click(object x) { ShowSection("new", "Nova"); }\n'
                'private void OpenMissionControlButton_Click(object x) { ShowSection("mission", "Mission"); }\n'
                'private void OpenActivityButton_Click(object x) { ShowSection("activity", "Activity"); }',
                encoding="utf-8",
            )
            xaml.write_text('Content="Ir ao Command Center sem concluir"', encoding="utf-8")

            payload = MODULE.audit(root, "1" * 40)
            self.assertEqual(payload["store"]["status"], MODULE.PASS)
            self.assertEqual(payload["sourcePreconditionStatus"], MODULE.BLOCKED)
            self.assertTrue(payload["unconditionalBypassLabelDetected"])
            self.assertEqual(payload["finalAvaStatus"], "NOT_RUN")

    def test_fully_wired_source_is_only_partial_not_final_ava_pass(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            store = root / "apps/aevrix-windows/src/AEVRIX.Core/FirstRunAcceptanceStore.cs"
            tests = root / "apps/aevrix-windows/tests/AEVRIX.Core.Tests/FirstRunAcceptanceStoreTests.cs"
            cs = root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml.cs"
            xaml = root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml"
            for path in (store, tests, cs, xaml):
                path.parent.mkdir(parents=True, exist_ok=True)
            store.write_text(
                "CurrentSchemaVersion CurrentTermsRevision return false JsonException File.Move overwrite: true",
                encoding="utf-8",
            )
            tests.write_text(
                "StaleRevision_IsRejected MissingOrMalformedAcceptance_IsFailClosed",
                encoding="utf-8",
            )
            guarded = "if (!FirstRunAcceptanceStore.IsAccepted()) { return; }"
            cs.write_text(
                "FirstRunAcceptanceStore store; bool ok = store.IsAccepted();\n"
                f'private void RootNavigation_SelectionChanged(object x) {{ {guarded} ShowSection("home", "Command Center"); }}\n'
                f'private void BackToHomeButton_Click(object x) {{ {guarded} ShowSection("home", "Command Center"); }}\n'
                f'private void StartAnalysisButton_Click(object x) {{ {guarded} ShowSection("new", "Nova"); }}\n'
                f'private void OpenMissionControlButton_Click(object x) {{ {guarded} ShowSection("mission", "Mission"); }}\n'
                f'private void OpenActivityButton_Click(object x) {{ {guarded} ShowSection("activity", "Activity"); }}',
                encoding="utf-8",
            )
            xaml.write_text('Text="Termos" Content="Aceitar" Content="Recusar e sair"', encoding="utf-8")

            payload = MODULE.audit(root, "2" * 40)
            self.assertEqual(payload["sourcePreconditionStatus"], MODULE.PARTIAL)
            self.assertEqual(payload["finalAvaStatus"], "NOT_RUN")


if __name__ == "__main__":
    unittest.main()
