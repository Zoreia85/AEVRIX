from __future__ import annotations

from dataclasses import asdict, dataclass, field
from typing import Any


@dataclass(frozen=True)
class EvidenceItem:
    kind: str
    value: str
    confidence: float
    source: str

    def __post_init__(self) -> None:
        if not 0.0 <= self.confidence <= 1.0:
            raise ValueError("confidence must be between 0.0 and 1.0")


@dataclass(frozen=True)
class ArchiveSafety:
    entry_count: int
    total_uncompressed_bytes: int
    max_compression_ratio: float
    unsafe_paths: tuple[str, ...] = ()
    policy_violations: tuple[str, ...] = ()

    @property
    def safe_for_inventory(self) -> bool:
        return not self.unsafe_paths and not self.policy_violations


@dataclass(frozen=True)
class ArtifactReport:
    schema_version: int
    artifact_name: str
    artifact_size_bytes: int
    sha256: str
    format: str
    platform: str
    confidence: float
    evidence: tuple[EvidenceItem, ...] = ()
    archive_safety: ArchiveSafety | None = None
    metadata: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True)
class MetricResult:
    name: str
    numerator: int | None
    denominator: int | None
    percent: float | None
    status: str


@dataclass(frozen=True)
class ReconstructionScorecard:
    metrics: tuple[MetricResult, ...]
    critical_divergences_open: int | None
    homologation_status: str

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)
