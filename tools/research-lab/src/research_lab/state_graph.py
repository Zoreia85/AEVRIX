from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass, field
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit


_VOLATILE_QUERY_KEYS = {
    "_",
    "cache",
    "cachebuster",
    "nonce",
    "timestamp",
    "ts",
}


def _normalize_space(value: str) -> str:
    return re.sub(r"\s+", " ", value or "").strip()


def _normalized_url(url: str) -> str:
    parsed = urlsplit(url)
    query = [
        (k, v)
        for k, v in parse_qsl(parsed.query, keep_blank_values=True)
        if k.lower() not in _VOLATILE_QUERY_KEYS
    ]
    query.sort()
    return urlunsplit((parsed.scheme.lower(), parsed.netloc.lower(), parsed.path or "/", urlencode(query), ""))


@dataclass(frozen=True, slots=True)
class ControlSignature:
    role: str
    label: str
    href: str = ""

    def canonical(self) -> tuple[str, str, str]:
        return (
            _normalize_space(self.role).lower(),
            _normalize_space(self.label)[:240],
            _normalized_url(self.href) if self.href.startswith(("http://", "https://")) else self.href.strip(),
        )


@dataclass(frozen=True, slots=True)
class ApplicationState:
    url: str
    frame_path: tuple[str, ...] = ()
    active_menu: tuple[str, ...] = ()
    active_tabs: tuple[str, ...] = ()
    open_modals: tuple[str, ...] = ()
    filters: tuple[tuple[str, str], ...] = ()
    pagination: tuple[tuple[str, str], ...] = ()
    controls: tuple[ControlSignature, ...] = ()
    body_text: str = ""
    network_schema_keys: tuple[str, ...] = ()
    metadata: dict[str, str] = field(default_factory=dict, compare=False, hash=False)

    def canonical_payload(self) -> dict[str, object]:
        controls = sorted({control.canonical() for control in self.controls})
        return {
            "url": _normalized_url(self.url),
            "frame_path": [_normalize_space(x) for x in self.frame_path],
            "active_menu": [_normalize_space(x) for x in self.active_menu],
            "active_tabs": [_normalize_space(x) for x in self.active_tabs],
            "open_modals": [_normalize_space(x) for x in self.open_modals],
            "filters": sorted((_normalize_space(k), _normalize_space(v)) for k, v in self.filters),
            "pagination": sorted((_normalize_space(k), _normalize_space(v)) for k, v in self.pagination),
            "controls": [list(item) for item in controls],
            "body_text": _normalize_space(self.body_text)[:160_000],
            "network_schema_keys": sorted(set(self.network_schema_keys)),
        }

    @property
    def state_id(self) -> str:
        raw = json.dumps(self.canonical_payload(), ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(raw.encode()).hexdigest()


@dataclass(frozen=True, slots=True)
class StateTransition:
    from_state_id: str
    to_state_id: str
    action_key: str
    action_label: str = ""

    def __post_init__(self) -> None:
        if not self.from_state_id or not self.to_state_id or not self.action_key.strip():
            raise ValueError("state transition requires source, destination and action_key")
