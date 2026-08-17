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
    window_cs_path = repo_root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml.cs"
    window_xaml_path = repo_root / "apps/aevrix-windows/src/AEVRIX.Desktop/MainWindow.xaml"

    store = read_text(store_path)
    tests = read_text(tests_path)
    window_cs = read_text(window_cs_path)
    window_xaml = read_text(window_xaml_path)

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

    route_methods = {
        "navigationSelection": method_body(window_cs, "RootNavigation_SelectionChanged"),
        "backToHome": method_body(window_cs, "BackToHomeButton_Click"),
        "startAnalysis": method_body(window_cs, "StartAnalysisButton_Click"),
        "openMissionControl": method_body(window_cs, "OpenMissionControlButton_Click"),
        "openActivity": method_body(window_cs, "OpenActivityButton_Click"),
    }
    route_checks = {name: contains_terms_guard(body) for name, body in route_methods.items()}
    desktop_binding = "FirstRunAcceptanceStore" in window_cs and "IsAccepted(" in window_cs

    xaml_lower = window_xaml.lower()
    terms_surface_signals = {
        "termsLanguage": "termos" in xaml_lower or "terms" in xaml_lower,
        "acceptAction": "aceit" in xaml_lower or "accept" in xaml_lower,
        "declineOrExitAction": any(token in xaml_lower for token in ("recusar", "decline", "sair", "exit")),
    }
    has_unconditional_bypass_label = "ir ao command center sem concluir" in xaml_lower

    routing_precondition = desktop_binding and all(route_checks.values()) and all(terms_surface_signals.values()) and not has_unconditional_bypass_label
    source_precondition_status = PARTIAL if store_status == PASS and routing_precondition else BLOCKED

    findings: list[str] = []
    if store_status == PASS:
        findings.append("Versioned first-run acceptance persistence and fail-closed store tests are physically present.")
    else:
        findings.append("Versioned first-run acceptance persistence is incomplete or lacks required source tests.")
    if not desktop_binding:
        findings.append("Desktop MainWindow is not physically bound to FirstRunAcceptanceStore.IsAccepted().")
    for route, guarded in route_checks.items():
        if not guarded:
            findings.append(f"Operational routing method '{route}' has no detectable terms-acceptance guard.")
    if has_unconditional_bypass_label:
        findings.append("XAML exposes 'Ir ao Command Center sem concluir', which is incompatible with a mandatory terms gate until routing is fail-closed.")
    if not all(terms_surface_signals.values()):
        findings.append("A complete accept + decline/exit terms surface is not detectable in MainWindow.xaml.")

    files = []
    for path in (store_path, tests_path, window_cs_path, window_xaml_path):
        if path.is_file():
            files.append({
                "path": path.relative_to(repo_root).as_posix(),
                "sha256": sha256_file(path),
                "sizeBytes": path.stat().st_size,
            })

    return {
        "schemaVersion": 1,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "sourceCommit": source_commit,
        "scope": "STATIC_SOURCE_PRECONDITION_ONLY_NOT_FINAL_AVA",
        "store": {"status": store_status, "checks": store_checks},
        "desktopBinding": desktop_binding,
        "routeGuards": route_checks,
        "termsSurface": terms_surface_signals,
        "unconditionalBypassLabelDetected": has_unconditional_bypass_label,
        "sourcePreconditionStatus": source_precondition_status,
        "finalAvaStatus": "NOT_RUN",
        "findings": findings,
        "files": files,
        "rule": "Static source analysis can prove missing preconditions but can never by itself satisfy the mandatory real-Windows terms/first-run AVA gate.",
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
