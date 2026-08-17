#!/usr/bin/env python3
"""Cross-check AEVRIX external capability governance coverage.

This gate prevents an external Adapter/OptionalTool surface from existing in the
Repository Intelligence manifest without an owner in the continuous capability
registry. It also verifies that declared implementation paths are present in the
checked-out candidate.

No quality score is inferred here. Benchmark evidence remains the only source of
capability scores.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any

EXECUTABLE_MODES = {"Adapter", "OptionalTool"}
IMPLEMENTED_STATES = {"LAB", "CONDITIONAL", "ADMITTED", "PREFERRED", "WATCH"}


def load_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def external_execution_candidates(manifest: dict[str, Any]) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    for entry in manifest.get("repositories", []):
        repository = str(entry.get("repository", "")).strip()
        modes = set(entry.get("integrationModes") or [])
        approval = str(entry.get("runtimeApproval", "Denied"))
        if repository and modes.intersection(EXECUTABLE_MODES) and approval != "Denied":
            result[repository] = entry
    return result


def validate(
    registry: dict[str, Any],
    manifest: dict[str, Any],
    root: Path,
) -> tuple[list[str], list[str], dict[str, list[str]]]:
    errors: list[str] = []
    notes: list[str] = []
    ownership: dict[str, list[str]] = defaultdict(list)

    capabilities = registry.get("capabilities")
    if not isinstance(capabilities, list) or not capabilities:
        return ["capability registry must contain a non-empty capabilities array"], notes, ownership

    known_ids: set[str] = set()
    for cap in capabilities:
        cap_id = str(cap.get("id", "")).strip()
        if not cap_id:
            errors.append("capability without id")
            continue
        if cap_id in known_ids:
            errors.append(f"duplicate capability id: {cap_id}")
        known_ids.add(cap_id)

        for repository in cap.get("source_repositories") or []:
            repository = str(repository).strip()
            if repository:
                ownership[repository].append(cap_id)

        implementation_paths = cap.get("implementation_paths") or []
        if not isinstance(implementation_paths, list):
            errors.append(f"{cap_id}: implementation_paths must be an array")
            implementation_paths = []

        for raw_path in implementation_paths:
            relative = Path(str(raw_path))
            if relative.is_absolute() or ".." in relative.parts:
                errors.append(f"{cap_id}: unsafe implementation path {raw_path!r}")
                continue
            if not (root / relative).is_file():
                errors.append(f"{cap_id}: declared implementation path is missing: {raw_path}")

        state = str(cap.get("state", ""))
        evaluation_status = str(cap.get("evaluation_status", ""))
        if state in IMPLEMENTED_STATES and evaluation_status.startswith("IMPLEMENTED") and not implementation_paths:
            errors.append(f"{cap_id}: implemented lifecycle entry requires implementation_paths")

        if cap.get("scores") is None and float(cap.get("evidence_confidence", 0.0)) > 0.0:
            errors.append(f"{cap_id}: evidence_confidence cannot be positive while scores are unmeasured")

    candidates = external_execution_candidates(manifest)
    for repository, entry in sorted(candidates.items()):
        owners = ownership.get(repository, [])
        if not owners:
            errors.append(
                f"{repository}: external Adapter/OptionalTool surface has runtimeApproval="
                f"{entry.get('runtimeApproval')} but no capability registry owner"
            )
        elif len(owners) > 1:
            errors.append(f"{repository}: multiple capability registry owners: {', '.join(sorted(owners))}")
        else:
            notes.append(f"{repository}: governed by {owners[0]}")

    manifest_repositories = {str(x.get("repository", "")) for x in manifest.get("repositories", [])}
    for repository in sorted(ownership):
        if repository not in manifest_repositories:
            notes.append(f"{repository}: registry source is not present in repository-intelligence manifest")

    return errors, notes, ownership


def render_report(
    registry: dict[str, Any],
    manifest: dict[str, Any],
    errors: list[str],
    notes: list[str],
) -> str:
    candidates = external_execution_candidates(manifest)
    capabilities = registry.get("capabilities") or []
    implemented_unmeasured = [
        str(cap.get("id"))
        for cap in capabilities
        if str(cap.get("evaluation_status", "")).startswith("IMPLEMENTED")
        and cap.get("scores") is None
    ]

    lines = [
        "# AEVRIX External Capability Coverage Report",
        "",
        f"Registry schema: `{registry.get('schema_version', 'unknown')}`",
        f"External executable/candidate manifest surfaces: **{len(candidates)}**",
        f"Registry capabilities: **{len(capabilities)}**",
        f"Implemented but unmeasured capabilities: **{len(implemented_unmeasured)}**",
        "",
        "## Manifest surfaces requiring registry ownership",
        "",
        "| Repository | Runtime approval | Integration modes |",
        "|---|---|---|",
    ]

    for repository, entry in sorted(candidates.items()):
        modes = ", ".join(entry.get("integrationModes") or [])
        lines.append(f"| {repository} | {entry.get('runtimeApproval')} | {modes} |")

    lines.extend(["", "## Coverage observations", ""])
    if notes:
        lines.extend(f"- {note}" for note in notes)
    else:
        lines.append("- No coverage observations.")

    lines.extend(["", "## Implemented but unmeasured", ""])
    if implemented_unmeasured:
        lines.extend(f"- `{cap_id}` — no score assigned until benchmark evidence exists." for cap_id in implemented_unmeasured)
    else:
        lines.append("- None.")

    lines.extend(["", "## Gate result", ""])
    if errors:
        lines.append("**FAIL**")
        lines.extend(f"- {error}" for error in errors)
    else:
        lines.append("**PASS** — every eligible external manifest surface is owned by exactly one capability registry entry and declared implementation paths exist.")

    lines.extend(
        [
            "",
            "This report proves governance coverage only. It does not prove efficacy, precision, safety, cost-benefit, production admission or homologation.",
            "",
        ]
    )
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--registry", default="docs/qa/capability-registry.json")
    parser.add_argument("--manifest", default="docs/manifests/repository-intelligence.json")
    parser.add_argument("--root", default=".")
    parser.add_argument("--out", default="capability-coverage-report.md")
    parser.add_argument("--strict", action="store_true")
    args = parser.parse_args()

    registry = load_json(Path(args.registry))
    manifest = load_json(Path(args.manifest))
    errors, notes, _ = validate(registry, manifest, Path(args.root).resolve())
    report = render_report(registry, manifest, errors, notes)
    Path(args.out).write_text(report, encoding="utf-8")
    print(report)

    return 1 if errors and args.strict else 0


if __name__ == "__main__":
    sys.exit(main())
