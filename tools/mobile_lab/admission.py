from __future__ import annotations

from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

from .artifact_intelligence import inspect_artifact

SUPPORTED_MOBILE_FORMATS = {"APK", "AAB", "XAPK", "IPA"}
MIN_STRUCTURAL_CONFIDENCE = 0.90


@dataclass(frozen=True)
class MobileArtifactAdmission:
    schema_version: int
    admitted: bool
    decision: str
    format: str
    platform: str
    sha256: str
    reasons: tuple[str, ...]
    evidence_confidence: float
    safe_for_inventory: bool

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def evaluate_mobile_artifact(path_like: str | Path) -> MobileArtifactAdmission:
    """Fail-closed preflight for APK/AAB/XAPK/IPA before deeper parsing/execution.

    File extensions are never sufficient for admission. Admission requires a
    structurally recognized mobile container, a safe archive inventory and high
    structural confidence. This function performs no extraction, execution,
    signature bypass or access-control bypass.
    """

    report = inspect_artifact(path_like)
    reasons: list[str] = []

    if report.format not in SUPPORTED_MOBILE_FORMATS:
        reasons.append("unsupported_or_unclassified_mobile_format")

    safety = report.archive_safety
    safe_for_inventory = bool(safety and safety.safe_for_inventory)
    if safety is None:
        reasons.append("mobile_container_not_verified_as_archive")
    elif not safety.safe_for_inventory:
        if safety.unsafe_paths:
            reasons.append("archive_contains_unsafe_paths")
        reasons.extend(safety.policy_violations)

    if report.confidence < MIN_STRUCTURAL_CONFIDENCE:
        reasons.append("structural_confidence_below_admission_threshold")

    if report.platform not in {"android", "ios"}:
        reasons.append("mobile_platform_unresolved")

    admitted = not reasons
    return MobileArtifactAdmission(
        schema_version=1,
        admitted=admitted,
        decision="ADMIT_INVENTORY_ONLY" if admitted else "BLOCK",
        format=report.format,
        platform=report.platform,
        sha256=report.sha256,
        reasons=tuple(dict.fromkeys(reasons)),
        evidence_confidence=report.confidence,
        safe_for_inventory=safe_for_inventory,
    )
