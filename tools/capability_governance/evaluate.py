#!/usr/bin/env python3
"""AEVRIX capability-governance score evaluator.

Standard-library only. It validates the machine-readable capability registry,
computes evidence-weighted score bands when measured scores exist, and rejects
unsafe lifecycle promotion when the evidence/confidence gates are not met.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any


ALLOWED_STATES = {
    "CANDIDATE",
    "LAB",
    "CONDITIONAL",
    "ADMITTED",
    "PREFERRED",
    "WATCH",
    "QUARANTINED",
    "REMOVE",
    "REJECTED",
}

PRODUCTION_STATES = {"CONDITIONAL", "ADMITTED", "PREFERRED"}
CONFIDENCE_MIN = {
    "CONDITIONAL": 0.60,
    "ADMITTED": 0.75,
    "PREFERRED": 0.85,
}


@dataclass(frozen=True)
class Evaluation:
    raw_score: float | None
    confidence: float
    evidence_adjusted_score: float | None
    eligible_band: str


def load_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def band_for(score: float | None, confidence: float, hard_gates: str) -> str:
    if score is None:
        return "UNMEASURED"
    if hard_gates != "PASS":
        return "LAB_ONLY"
    if score >= 85 and confidence >= 0.85:
        return "PREFERRED"
    if score >= 75 and confidence >= 0.75:
        return "ADMITTED"
    if score >= 60 and confidence >= 0.60:
        return "CONDITIONAL"
    if score >= 45:
        return "WATCH"
    return "REMOVE_OR_REJECT"


def evaluate_capability(cap: dict[str, Any], weights: dict[str, int]) -> Evaluation:
    confidence = float(cap.get("evidence_confidence", 0.0))
    if not 0.0 <= confidence <= 1.0:
        raise ValueError(f"{cap.get('id')}: evidence_confidence must be 0.0-1.0")

    scores = cap.get("scores")
    if scores is None:
        return Evaluation(None, confidence, None, "UNMEASURED")

    missing = set(weights) - set(scores)
    extra = set(scores) - set(weights)
    if missing or extra:
        raise ValueError(
            f"{cap.get('id')}: score keys mismatch; missing={sorted(missing)} extra={sorted(extra)}"
        )

    weighted_sum = 0.0
    for key, weight in weights.items():
        value = float(scores[key])
        if not 0.0 <= value <= 10.0:
            raise ValueError(f"{cap.get('id')}: {key} score must be 0-10")
        weighted_sum += value * weight

    raw = weighted_sum / 10.0
    adjusted = raw * confidence
    band = band_for(raw, confidence, str(cap.get("hard_gates", "PENDING")))
    return Evaluation(round(raw, 2), confidence, round(adjusted, 2), band)


def validate_registry(data: dict[str, Any]) -> tuple[list[tuple[dict[str, Any], Evaluation]], list[str]]:
    errors: list[str] = []
    weights = data.get("weights")
    if not isinstance(weights, dict) or not weights:
        return [], ["registry weights are missing"]

    if sum(int(v) for v in weights.values()) != 100:
        errors.append("score weights must sum to exactly 100")

    caps = data.get("capabilities")
    if not isinstance(caps, list) or not caps:
        errors.append("capabilities must be a non-empty array")
        return [], errors

    seen: set[str] = set()
    evaluated: list[tuple[dict[str, Any], Evaluation]] = []

    for cap in caps:
        cap_id = str(cap.get("id", "")).strip()
        if not cap_id:
            errors.append("capability without id")
            continue
        if cap_id in seen:
            errors.append(f"duplicate capability id: {cap_id}")
            continue
        seen.add(cap_id)

        state = str(cap.get("state", ""))
        if state not in ALLOWED_STATES:
            errors.append(f"{cap_id}: unsupported lifecycle state {state!r}")

        try:
            result = evaluate_capability(cap, weights)
            evaluated.append((cap, result))
        except (TypeError, ValueError) as exc:
            errors.append(str(exc))
            continue

        if state in PRODUCTION_STATES:
            if cap.get("hard_gates") != "PASS":
                errors.append(f"{cap_id}: {state} requires hard_gates=PASS")
            minimum = CONFIDENCE_MIN[state]
            if result.confidence < minimum:
                errors.append(
                    f"{cap_id}: {state} requires evidence_confidence >= {minimum:.2f}"
                )
            if result.eligible_band not in {state, "PREFERRED"}:
                errors.append(
                    f"{cap_id}: lifecycle {state} exceeds measured eligible band {result.eligible_band}"
                )

        if state == "PREFERRED" and result.eligible_band != "PREFERRED":
            errors.append(f"{cap_id}: PREFERRED requires a >=85 measured score and >=0.85 confidence")

    return evaluated, errors


def make_report(rows: list[tuple[dict[str, Any], Evaluation]], errors: list[str]) -> str:
    lines = [
        "# AEVRIX Capability Governance Report",
        "",
        "| Capability | State | Raw score | Evidence confidence | Evidence-adjusted | Eligible band | Hard gates | Recommendation |",
        "|---|---:|---:|---:|---:|---:|---:|---|",
    ]

    for cap, result in rows:
        raw = "—" if result.raw_score is None else f"{result.raw_score:.2f}"
        adjusted = (
            "—" if result.evidence_adjusted_score is None else f"{result.evidence_adjusted_score:.2f}"
        )
        lines.append(
            "| {name} | {state} | {raw} | {conf:.2f} | {adjusted} | {band} | {gates} | {rec} |".format(
                name=cap.get("name", cap.get("id")),
                state=cap.get("state", ""),
                raw=raw,
                conf=result.confidence,
                adjusted=adjusted,
                band=result.eligible_band,
                gates=cap.get("hard_gates", ""),
                rec=cap.get("recommendation", ""),
            )
        )

    lines.extend(["", "## Governance status", ""])
    if errors:
        lines.append("**FAIL**")
        for error in errors:
            lines.append(f"- {error}")
    else:
        lines.append("**PASS** — registry structure and lifecycle gates are internally consistent.")

    lines.extend(
        [
            "",
            "Unmeasured candidates are intentionally not assigned a synthetic score. They must earn a score from benchmark evidence before promotion.",
            "",
        ]
    )
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--registry",
        default="docs/qa/capability-registry.json",
        help="Path to the capability registry JSON",
    )
    parser.add_argument(
        "--out",
        default="capability-governance-report.md",
        help="Markdown report output path",
    )
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Return non-zero on governance validation errors",
    )
    args = parser.parse_args()

    registry_path = Path(args.registry)
    data = load_json(registry_path)
    rows, errors = validate_registry(data)
    report = make_report(rows, errors)
    Path(args.out).write_text(report, encoding="utf-8")
    print(report)

    if errors and args.strict:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
