#!/usr/bin/env python3
from __future__ import annotations

import json
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "docs" / "manifests" / "repository-intelligence.json"
CAPABILITY_REGISTRY = ROOT / "docs" / "qa" / "capability-registry.json"
EXECUTABLE_MODES = {"Adapter", "OptionalTool", "Vendored"}
IMPLEMENTED_STATES = {"LAB", "CONDITIONAL", "ADMITTED", "PREFERRED", "WATCH"}
ADMITTED_STATES = {"ADMITTED", "PREFERRED", "WATCH"}


def _load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def audit_capability_provenance(
    manifest: dict[str, Any],
    registry: dict[str, Any],
    root: Path = ROOT,
) -> list[str]:
    failures: list[str] = []
    if registry.get("schema_version") != 2:
        failures.append("capability registry schema_version must be 2")

    weights = registry.get("weights")
    if not isinstance(weights, dict) or not weights:
        failures.append("capability registry weights must be a non-empty object")
    else:
        numeric_weights = [value for value in weights.values() if isinstance(value, (int, float)) and not isinstance(value, bool)]
        if len(numeric_weights) != len(weights) or abs(sum(numeric_weights) - 100.0) > 1e-9:
            failures.append("capability registry weights must be numeric and sum to 100")

    capabilities = registry.get("capabilities")
    if not isinstance(capabilities, list) or not capabilities:
        return failures + ["capability registry capabilities must be a non-empty array"]

    known_ids: set[str] = set()
    ownership: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for index, capability in enumerate(capabilities):
        if not isinstance(capability, dict):
            failures.append(f"capabilities[{index}] must be an object")
            continue
        cap_id = str(capability.get("id", "")).strip()
        if not cap_id:
            failures.append(f"capabilities[{index}].id is required")
            continue
        if cap_id in known_ids:
            failures.append(f"duplicate capability id: {cap_id}")
        known_ids.add(cap_id)

        sources = capability.get("source_repositories") or []
        if not isinstance(sources, list) or any(not isinstance(source, str) or not source.strip() for source in sources):
            failures.append(f"{cap_id}: source_repositories must be an array of non-empty strings")
            sources = []
        for source in sources:
            ownership[source].append(capability)

        paths = capability.get("implementation_paths") or []
        if not isinstance(paths, list):
            failures.append(f"{cap_id}: implementation_paths must be an array")
            paths = []
        for raw_path in paths:
            if not isinstance(raw_path, str) or not raw_path.strip():
                failures.append(f"{cap_id}: implementation_paths contains an invalid path")
                continue
            relative = Path(raw_path)
            if relative.is_absolute() or ".." in relative.parts:
                failures.append(f"{cap_id}: unsafe implementation path: {raw_path}")
                continue
            if not (root / relative).is_file():
                failures.append(f"{cap_id}: declared implementation path is missing: {raw_path}")

        state = str(capability.get("state", ""))
        evaluation_status = str(capability.get("evaluation_status", ""))
        scores = capability.get("scores")
        try:
            confidence = float(capability.get("evidence_confidence", 0.0))
        except (TypeError, ValueError):
            failures.append(f"{cap_id}: evidence_confidence must be numeric")
            confidence = 0.0

        if state in IMPLEMENTED_STATES and evaluation_status.startswith("IMPLEMENTED") and not paths:
            failures.append(f"{cap_id}: implemented lifecycle entry requires implementation_paths")
        if scores is None and confidence > 0.0:
            failures.append(f"{cap_id}: unmeasured capability cannot claim positive evidence_confidence")
        if state in ADMITTED_STATES:
            if scores is None:
                failures.append(f"{cap_id}: admitted capability requires measured scores")
            if str(capability.get("hard_gates")) != "PASS":
                failures.append(f"{cap_id}: admitted capability requires hard_gates=PASS")
            if confidence <= 0.0:
                failures.append(f"{cap_id}: admitted capability requires positive evidence_confidence")

    repositories = manifest.get("repositories")
    if not isinstance(repositories, list):
        return failures + ["repository intelligence manifest repositories must be an array"]

    manifest_names = {
        str(record.get("repository", ""))
        for record in repositories
        if isinstance(record, dict) and record.get("repository")
    }
    for source in sorted(ownership):
        if source not in manifest_names:
            failures.append(f"{source}: capability ownership references a repository absent from repository intelligence")

    for record in repositories:
        if not isinstance(record, dict):
            continue
        repository = str(record.get("repository", "")).strip()
        modes = set(record.get("integrationModes") or [])
        approval = str(record.get("runtimeApproval", "Denied"))
        if not repository or not modes.intersection(EXECUTABLE_MODES) or approval == "Denied":
            continue

        owners = ownership.get(repository, [])
        if len(owners) != 1:
            if not owners:
                failures.append(f"{repository}: executable/pending integration requires exactly one capability registry owner; found 0")
            else:
                owner_ids = ", ".join(sorted(str(owner.get("id")) for owner in owners))
                failures.append(f"{repository}: executable/pending integration has multiple capability owners: {owner_ids}")
            continue

        owner = owners[0]
        cap_id = str(owner.get("id"))
        if approval == "Approved":
            if str(owner.get("state")) not in ADMITTED_STATES:
                failures.append(f"{repository}: Approved runtime requires admitted capability lifecycle state ({cap_id})")
            if owner.get("scores") is None:
                failures.append(f"{repository}: Approved runtime requires measured capability scores ({cap_id})")
            if str(owner.get("hard_gates")) != "PASS":
                failures.append(f"{repository}: Approved runtime requires capability hard_gates=PASS ({cap_id})")

    return failures


def main() -> int:
    try:
        manifest = _load_json(MANIFEST)
        registry = _load_json(CAPABILITY_REGISTRY)
    except (OSError, json.JSONDecodeError) as exc:
        print(json.dumps({"status": "FAIL", "failures": [str(exc)]}, indent=2), file=sys.stderr)
        return 1

    failures = audit_capability_provenance(manifest, registry, ROOT)
    print(json.dumps({
        "status": "PASS" if not failures else "FAIL",
        "manifestRepositories": len(manifest.get("repositories") or []),
        "capabilities": len(registry.get("capabilities") or []),
        "failures": failures,
    }, indent=2, ensure_ascii=False))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
