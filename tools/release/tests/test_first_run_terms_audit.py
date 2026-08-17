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

    def _write_common(self, root: Path) -> dict[str, Path]:
        paths = {
            "store": root / "apps/aevrix-windows/src/AEVRIX.Core/FirstRunAcceptanceStore.cs",
            "tests": root / "apps/aevrix-windows/tests/AEVRIX.Core.Tests/FirstRunAcceptanceStoreTests.cs",
            "app": root / "apps/aevrix-windows/src/AEVRIX.Desktop/App.xaml.cs",
            "first_cs": root / "apps/aevrix-windows/src/AEVRIX.Desktop/FirstRunWindow.xaml.cs",
            "first_xaml": root / "apps/aevrix-windows/src/AEVRIX.Desktop/FirstRunWindow.xaml",
            "main_cs": root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml.cs",
            "main_xaml": root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml",
        }
        for path in paths.values():
            path.parent.mkdir(parents=True, exist_ok=True)
        paths["store"].write_text(
            "CurrentSchemaVersion CurrentTermsRevision return false JsonException File.Move overwrite: true",
            encoding="utf-8",
        )
        paths["tests"].write_text(
            "StaleRevision_IsRejected MissingOrMalformedAcceptance_IsFailClosed",
            encoding="utf-8",
        )
        paths["main_cs"].write_text(
            'private void RootNavigation_SelectionChanged(object x) { ShowSection("home", "Command Center"); }\n'
            'private void BackToHomeButton_Click(object x) { ShowSection("home", "Command Center"); }\n'
            'private void StartAnalysisButton_Click(object x) { ShowSection("new", "Nova"); }\n'
            'private void OpenMissionControlButton_Click(object x) { ShowSection("mission", "Mission"); }\n'
            'private void OpenActivityButton_Click(object x) { ShowSection("activity", "Activity"); }',
            encoding="utf-8",
        )
        paths["main_xaml"].write_text('Content="Ir ao Command Center sem concluir"', encoding="utf-8")
        return paths

    def test_missing_launch_gate_is_blocked(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            paths = self._write_common(root)
            paths["app"].write_text("OpenMainWindow();", encoding="utf-8")
            paths["first_cs"].write_text("", encoding="utf-8")
            paths["first_xaml"].write_text("", encoding="utf-8")

            payload = MODULE.audit(root, "1" * 40)
            self.assertEqual(payload["store"]["status"], MODULE.PASS)
            self.assertFalse(payload["launchGateBinding"])
            self.assertEqual(payload["sourcePreconditionStatus"], MODULE.BLOCKED)
            self.assertTrue(payload["unconditionalBypassLabelDetected"])
            self.assertEqual(payload["finalAvaStatus"], "NOT_RUN")

    def test_launch_bound_gate_is_partial_even_when_main_handlers_do_not_repeat_terms_guard(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            paths = self._write_common(root)
            paths["app"].write_text(
                "var firstRunStore = new FirstRunAcceptanceStore(root);\n"
                "if (firstRunStore.IsAccepted()) { OpenMainWindow(); return; }\n"
                "_window = new FirstRunWindow(firstRunStore, OpenMainWindow);",
                encoding="utf-8",
            )
            paths["first_cs"].write_text(
                "private void FirstRunWindow_Activated(object s, object e) { _store.RecordPresentation(); }\n"
                "private void AcceptFirstRunButton_Click(object s, object e) { _store.Accept(); if (!_store.IsAccepted()) { return; } _continueToProduct(); Close(); }\n"
                "private void DeclineFirstRunButton_Click(object s, object e) { Close(); }",
                encoding="utf-8",
            )
            paths["first_xaml"].write_text(
                'Text="Termos e condições" AutomationProperties.AutomationId="AevrixFirstRunConfirm" '
                'AutomationProperties.AutomationId="AevrixFirstRunAccept" '
                'AutomationProperties.AutomationId="AevrixFirstRunDecline"',
                encoding="utf-8",
            )

            payload = MODULE.audit(root, "2" * 40)
            self.assertTrue(payload["launchGateBinding"])
            self.assertEqual(payload["sourcePreconditionStatus"], MODULE.PARTIAL)
            self.assertFalse(payload["unconditionalBypassLabelDetected"])
            self.assertTrue(payload["mainWindowPostTermsBypassLabelDetected"])
            self.assertEqual(payload["finalAvaStatus"], "NOT_RUN")

    def test_decline_that_continues_to_product_is_blocked(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            paths = self._write_common(root)
            paths["app"].write_text(
                "var firstRunStore = new FirstRunAcceptanceStore(root); if (firstRunStore.IsAccepted()) { OpenMainWindow(); return; } _window = new FirstRunWindow(firstRunStore, OpenMainWindow);",
                encoding="utf-8",
            )
            paths["first_cs"].write_text(
                "private void FirstRunWindow_Activated(object s, object e) { _store.RecordPresentation(); }\n"
                "private void AcceptFirstRunButton_Click(object s, object e) { _store.Accept(); if (!_store.IsAccepted()) { return; } _continueToProduct(); Close(); }\n"
                "private void DeclineFirstRunButton_Click(object s, object e) { _continueToProduct(); Close(); }",
                encoding="utf-8",
            )
            paths["first_xaml"].write_text(
                'Text="Termos" AutomationProperties.AutomationId="AevrixFirstRunConfirm" '
                'AutomationProperties.AutomationId="AevrixFirstRunAccept" '
                'AutomationProperties.AutomationId="AevrixFirstRunDecline"',
                encoding="utf-8",
            )

            payload = MODULE.audit(root, "3" * 40)
            self.assertFalse(payload["firstRunLogic"]["declineDoesNotContinue"])
            self.assertEqual(payload["sourcePreconditionStatus"], MODULE.BLOCKED)

    def test_static_source_can_never_claim_final_ava_pass(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            paths = self._write_common(root)
            paths["app"].write_text(
                "var firstRunStore = new FirstRunAcceptanceStore(root); if (firstRunStore.IsAccepted()) { OpenMainWindow(); return; } _window = new FirstRunWindow(firstRunStore, OpenMainWindow);",
                encoding="utf-8",
            )
            paths["first_cs"].write_text(
                "private void FirstRunWindow_Activated(object s, object e) { _store.RecordPresentation(); }\n"
                "private void AcceptFirstRunButton_Click(object s, object e) { _store.Accept(); if (!_store.IsAccepted()) { return; } _continueToProduct(); Close(); }\n"
                "private void DeclineFirstRunButton_Click(object s, object e) { Close(); }",
                encoding="utf-8",
            )
            paths["first_xaml"].write_text(
                'Text="Termos" AutomationProperties.AutomationId="AevrixFirstRunConfirm" '
                'AutomationProperties.AutomationId="AevrixFirstRunAccept" '
                'AutomationProperties.AutomationId="AevrixFirstRunDecline"',
                encoding="utf-8",
            )

            payload = MODULE.audit(root, "4" * 40)
            self.assertEqual(payload["sourcePreconditionStatus"], MODULE.PARTIAL)
            self.assertEqual(payload["finalAvaStatus"], "NOT_RUN")


if __name__ == "__main__":
    unittest.main()
