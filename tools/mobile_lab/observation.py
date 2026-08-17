from __future__ import annotations

import hashlib
import json
import re
from dataclasses import asdict, dataclass, field
from datetime import datetime
from typing import Any

_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")


def _validate_sha256(name: str, value: str | None) -> None:
    if value is not None and not _SHA256_RE.fullmatch(value):
        raise ValueError(f"{name} must be a lowercase SHA-256 hex digest")


@dataclass(frozen=True)
class ObservationRecord:
    """Canonical dynamic observation suitable for linking to the Evidence Bus.

    The record contains references/hashes only; raw screenshots, logs, UI trees and
    traces remain separate evidence artifacts.
    """

    timestamp: str
    session_id: str
    sequence: int
    platform: str
    environment_id: str
    previous_state_id: str | None
    action: str
    next_state_id: str | None
    screenshot_sha256: str | None = None
    ui_tree_sha256: str | None = None
    log_sha256: str | None = None
    network_trace_sha256: str | None = None
    persistence_snapshot_sha256: str | None = None
    metrics: dict[str, float] = field(default_factory=dict)
    annotations: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        try:
            parsed = datetime.fromisoformat(self.timestamp.replace("Z", "+00:00"))
        except ValueError as exc:
            raise ValueError("timestamp must be ISO-8601") from exc
        if parsed.tzinfo is None or parsed.utcoffset() is None:
            raise ValueError("timestamp must include a timezone offset")
        if not self.session_id.strip() or not self.environment_id.strip():
            raise ValueError("session_id and environment_id are required")
        if self.sequence < 0:
            raise ValueError("sequence cannot be negative")
        if self.platform not in {"android", "ios"}:
            raise ValueError("platform must be android or ios")
        if not self.action.strip():
            raise ValueError("action is required")
        for name in (
            "screenshot_sha256",
            "ui_tree_sha256",
            "log_sha256",
            "network_trace_sha256",
            "persistence_snapshot_sha256",
        ):
            _validate_sha256(name, getattr(self, name))
        for key, value in self.metrics.items():
            if not isinstance(key, str) or not isinstance(value, (int, float)):
                raise ValueError("metrics must map string names to numeric values")

    @property
    def evidence_id(self) -> str:
        canonical = json.dumps(asdict(self), sort_keys=True, separators=(",", ":"), ensure_ascii=False)
        return "obs_" + hashlib.sha256(canonical.encode("utf-8")).hexdigest()[:24]

    def to_dict(self) -> dict[str, Any]:
        payload = asdict(self)
        payload["evidence_id"] = self.evidence_id
        payload["schema_version"] = 1
        return payload
