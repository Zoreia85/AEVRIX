#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path

PASS = "PASS"
PARTIAL = "PARCIAL"
BLOCKED = "BLOQUEADO"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig") if path.is_file() else ""


def method_body(source: str, name: str) -> str:
    marker = re.search(rf"\b{name}\s*\([^)]*\)\s*\{{", source)
    if not marker:
        return ""
    start = source.find("{", marker.start())
    depth = 0
    for index in range(start, len(source)):
        char = source[index]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return source[start + 1:index]
    return ""


def contains_terms_guard(text: str) -> bool:
    lowered = text.lower()
    signals = (
        "firstrunacceptancestore",
        "isaccepted(",
        "termsaccepted",
        "termsacceptance",
        "canaccessoperational",
        "canenterrout",
        "requiresacceptance",
    )
    return any(signal in lowered for signal in signals)


def audit(repo_root: Path, source_commit: str) -> dict:
    store_path = repo_root / "apps/aevrix-windows/src/AEVRIX.Core/FirstRunAcceptanceStore.cs"
    tests_path = repo_root / "apps/aevrix-windows/tests/AEVRIX.Core.Tests/FirstRunAcceptanceStoreTests.cs"
    app_path = repo_root / "apps/aevrix-windows/src/AEVRIX.Desktop/App.xaml.cs"
    first_run_cs_path = repo_root / "apps/aevrix-windows/src/AEVRIX.Desktop/FirstRunWindow.xaml.cs"
    first_run_xaml_path = repo_root / "apps/aevrix-windows/src/AEVRIX.Desktop/FirstRunWindow.xaml"
    main_cs_path = repo_root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml.cs"
    main_xaml_path = repo_root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml"

    store = read_text(store_path)
    tests = read_text(tests_path)
    app = read_text(app_path)
    first_run_cs = read_text(first_run_cs_path)
    first_run_xaml = read_text(first_run_xaml_path)
    main_cs = read_text(main_cs_path)
    main_xaml = read_text(main_xaml_path)

    store_checks = {
        "physicalStore": bool(store),
        "versionedSchema": "CurrentSchemaVersion" in store,
        "versionedTermsRevision": "CurrentTermsRevision" in store,
        "failClosedRead": "return false" in store and "JsonException" in store,
        "atomicWrite": "File.Move" in store and "overwrite: true" in store,
        "storeTests": bool(tests),
        "staleRevisionTest": "StaleRevision_IsRejected" in tests,
        "malformedStateTest": "MissingOrMalformedAcceptance_IsFailClosed" in tests,
    }
    store_status = PASS if all(store_checks.values()) else BLOCKED

    launch_checks = {
        "physicalAppLaunchBoundary": bool(app),
        "constructsAcceptanceStore": "FirstRunAcceptanceStore" in app,
        "checksCurrentAcceptance": "IsAccepted()" in app,
        "routesUnacceptedToFirstRunWindow": "FirstRunWindow" in app,
        "opensMainWindowOnlyThroughContinuation": "OpenMainWindow" in app,
    }
    launch_gate_binding = all(launch_checks.values())

    accept_body = method_body(first_run_cs, "AcceptFirstRunButton_Click")
    decline_body = method_body(first_run_cs, "DeclineFirstRunButton_Click")
    activated_body = method_body(first_run_cs, "FirstRunWindow_Activated")

    first_run_logic_checks = {
        "recordsPresentation": "RecordPresentation" in activated_body,
        "persistsAcceptance": "_store.Accept" in accept_body,
        "revalidatesAcceptance": "_store.IsAccepted" in accept_body,
        "continuesOnlyFromAcceptHandler": "_continueToProduct" in accept_body,
        "declineCloses": "Close()" in decline_body,
        "declineDoesNotContinue": "_continueToProduct" not in decline_body and "OpenMainWindow" not in decline_body,
    }

    first_run_xaml_lower = first_run_xaml.lower()
    terms_surface_signals = {
        "termsLanguage": "termos" in first_run_xaml_lower or "terms" in first_run_xaml_lower or "condições" in first_run_xaml_lower,
        "acceptAction": "aevrixfirstrunaccept" in first_run_xaml_lower,
        "declineOrExitAction": "aevrixfirstrundecline" in first_run_xaml_lower,
        "explicitConfirmation": "aevrixfirstrunconfirm" in first_run_xaml_lower,
    }

    route_methods = {
        "navigationSelection": method_body(main_cs, "RootNavigation_SelectionChanged"),
        "backToHome": method_body(main_cs, "BackToHomeButton_Click"),
        "startAnalysis": method_body(main_cs, "StartAnalysisButton_Click"),
        "openMissionControl": method_body(main_cs, "OpenMissionControlButton_Click"),
        "openActivity": method_body(main_cs, "OpenActivityButton_Click"),
    }
    route_checks = {name: contains_terms_guard(body) for name, body in route_methods.items()}
    main_window_post_terms_bypass_label = "ir ao command center sem concluir" in main_xaml.lower()

    source_ready = (
        store_status == PASS
        and launch_gate_binding
        and all(first_run_logic_checks.values())
        and all(terms_surface_signals.values())
    )
    source_precondition_status = PARTIAL if source_ready else BLOCKED

    findings: list[str] = []
    if store_status == PASS:
        findings.append("Versioned first-run acceptance persistence and fail-closed store tests are physically present.")
    else:
        findings.append("Versioned first-run acceptance persistence is incomplete or lacks required source tests.")

    if launch_gate_binding:
        findings.append("App launch is physically bound to FirstRunAcceptanceStore and routes unaccepted users to FirstRunWindow before MainWindow is created.")
    else:
        findings.append("The App launch boundary does not prove that MainWindow is unreachable before current terms acceptance.")

    if all(first_run_logic_checks.values()) and all(terms_surface_signals.values()):
        findings.append("A dedicated terms surface with explicit confirmation, accept, decline/exit, presentation recording and fail-closed acceptance revalidation is physically present.")
    else:
        findings.append("The dedicated first-run terms surface or its fail-closed accept/decline logic is incomplete.")

    if main_window_post_terms_bypass_label:
        if launch_gate_binding:
            findings.append("MainWindow still contains 'Ir ao Command Center sem concluir', but this is downstream of the launch-bound terms authority and is not itself a terms bypass. Its onboarding semantics remain a separate product/UX concern.")
        else:
            findings.append("MainWindow exposes 'Ir ao Command Center sem concluir' while no outer launch-bound terms authority is proven.")

    if launch_gate_binding and not all(route_checks.values()):
        findings.append("Individual MainWindow handlers do not repeat the terms check; this is acceptable as a source precondition only because MainWindow construction is gated at App.OnLaunched. Real Windows AVA must prove MainWindow is absent before acceptance.")

    files = []
    for path in (
        store_path,
        tests_path,
        app_path,
        first_run_cs_path,
        first_run_xaml_path,
        main_cs_path,
        main_xaml_path,
    ):
        if path.is_file():
            files.append({
                "path": path.relative_to(repo_root).as_posix(),
                "sha256": sha256_file(path),
                "sizeBytes": path.stat().st_size,
            })

    return {
        "schemaVersion": 2,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "sourceCommit": source_commit,
        "scope": "STATIC_SOURCE_PRECONDITION_ONLY_NOT_FINAL_AVA",
        "store": {"status": store_status, "checks": store_checks},
        "launchGateBinding": launch_gate_binding,
        "launchGateChecks": launch_checks,
        "desktopBinding": launch_gate_binding,
        "firstRunLogic": first_run_logic_checks,
        "termsSurface": terms_surface_signals,
        "routeGuards": route_checks,
        "mainWindowPostTermsBypassLabelDetected": main_window_post_terms_bypass_label,
        "unconditionalBypassLabelDetected": main_window_post_terms_bypass_label and not launch_gate_binding,
        "sourcePreconditionStatus": source_precondition_status,
        "finalAvaStatus": "NOT_RUN",
        "findings": findings,
        "files": files,
        "rule": "Static source analysis may prove launch-bound first-run/terms preconditions but can never by itself satisfy the mandatory exact-candidate real-Windows terms/first-run AVA gate.",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="AEVRIX first-run/terms fail-closed source precondition audit")
    parser.add_argument("--repo-root", default=".")
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--fail-if-blocked", action="store_true")
    args = parser.parse_args()

    payload = audit(Path(args.repo_root).resolve(), args.source_commit)
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(payload, indent=2, ensure_ascii=False))
    return 2 if args.fail_if_blocked and payload["sourcePreconditionStatus"] == BLOCKED else 0


if __name__ == "__main__":
    raise SystemExit(main())
