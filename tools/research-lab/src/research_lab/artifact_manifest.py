from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path


class ArtifactManifestError(ValueError):
    """Raised when an artifact manifest is unsafe or inconsistent."""


@dataclass(frozen=True, slots=True)
class ArtifactRecord:
    relative_path: str
    sha256: str
    size_bytes: int
    media_type: str
    classification: str

    def __post_init__(self) -> None:
        path = Path(self.relative_path)
        if path.is_absolute() or ".." in path.parts:
            raise ArtifactManifestError("artifact paths must be relative and contained")
        if len(self.sha256) != 64 or any(ch not in "0123456789abcdef" for ch in self.sha256.lower()):
            raise ArtifactManifestError("artifact sha256 is invalid")
        if self.size_bytes < 0:
            raise ArtifactManifestError("artifact size cannot be negative")
        if self.classification not in {"sanitized", "quarantine", "neutral-knowledge"}:
            raise ArtifactManifestError("unsupported artifact classification")


@dataclass(frozen=True, slots=True)
class CaptureManifest:
    capture_id: str
    target_id: str
    created_at: str
    artifacts: tuple[ArtifactRecord, ...]
    raw_artifacts_in_git: bool = False

    def __post_init__(self) -> None:
        if self.raw_artifacts_in_git:
            raise ArtifactManifestError("raw/heavy artifacts are forbidden in normal Git")
        if not self.capture_id.strip() or not self.target_id.strip():
            raise ArtifactManifestError("capture_id and target_id are required")
        paths = [record.relative_path for record in self.artifacts]
        if len(paths) != len(set(paths)):
            raise ArtifactManifestError("artifact paths must be unique")

    @classmethod
    def from_directory(
        cls,
        *,
        capture_id: str,
        target_id: str,
        directory: Path,
        classification: str = "sanitized",
    ) -> "CaptureManifest":
        directory = directory.resolve()
        records: list[ArtifactRecord] = []
        for path in sorted(p for p in directory.rglob("*") if p.is_file()):
            data = path.read_bytes()
            suffix = path.suffix.lower()
            media_type = {
                ".json": "application/json",
                ".md": "text/markdown",
                ".txt": "text/plain",
                ".png": "image/png",
                ".jpg": "image/jpeg",
                ".jpeg": "image/jpeg",
                ".csv": "text/csv",
            }.get(suffix, "application/octet-stream")
            records.append(
                ArtifactRecord(
                    relative_path=path.relative_to(directory).as_posix(),
                    sha256=hashlib.sha256(data).hexdigest(),
                    size_bytes=len(data),
                    media_type=media_type,
                    classification=classification,
                )
            )
        return cls(
            capture_id=capture_id,
            target_id=target_id,
            created_at=datetime.now(UTC).isoformat(timespec="seconds"),
            artifacts=tuple(records),
        )

    @property
    def total_size_bytes(self) -> int:
        return sum(record.size_bytes for record in self.artifacts)

    @property
    def manifest_sha256(self) -> str:
        payload = self.to_json().encode()
        return hashlib.sha256(payload).hexdigest()

    def to_json(self) -> str:
        return json.dumps(
            {
                "schemaVersion": 1,
                "captureId": self.capture_id,
                "targetId": self.target_id,
                "createdAt": self.created_at,
                "rawArtifactsInGit": self.raw_artifacts_in_git,
                "artifacts": [asdict(record) for record in self.artifacts],
            },
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        )
