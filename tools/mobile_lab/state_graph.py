from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass
from typing import Any


def _stable_id(prefix: str, payload: dict[str, Any]) -> str:
    canonical = json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return f"{prefix}_{hashlib.sha256(canonical.encode('utf-8')).hexdigest()[:16]}"


@dataclass(frozen=True)
class ObservedState:
    state_id: str
    label: str
    ui_tree_sha256: str | None
    screenshot_sha256: str | None
    attributes: dict[str, Any]


@dataclass(frozen=True)
class Transition:
    transition_id: str
    source_state_id: str
    action: str
    target_state_id: str
    evidence_sha256: str | None


class BehavioralStateGraph:
    def __init__(self) -> None:
        self._states: dict[str, ObservedState] = {}
        self._transitions: dict[str, Transition] = {}

    def add_state(
        self,
        label: str,
        *,
        ui_tree_sha256: str | None = None,
        screenshot_sha256: str | None = None,
        attributes: dict[str, Any] | None = None,
    ) -> ObservedState:
        payload = {
            "label": label,
            "ui_tree_sha256": ui_tree_sha256,
            "screenshot_sha256": screenshot_sha256,
            "attributes": attributes or {},
        }
        state = ObservedState(_stable_id("state", payload), **payload)
        self._states[state.state_id] = state
        return state

    def add_transition(
        self,
        source_state_id: str,
        action: str,
        target_state_id: str,
        *,
        evidence_sha256: str | None = None,
    ) -> Transition:
        if source_state_id not in self._states or target_state_id not in self._states:
            raise KeyError("transition endpoints must already exist")
        payload = {
            "source_state_id": source_state_id,
            "action": action,
            "target_state_id": target_state_id,
            "evidence_sha256": evidence_sha256,
        }
        transition = Transition(_stable_id("edge", payload), **payload)
        self._transitions[transition.transition_id] = transition
        return transition

    def coverage_percent(self, declared_total_states: int | None) -> float | None:
        if declared_total_states is None or declared_total_states <= 0:
            return None
        return round(min(100.0, len(self._states) * 100.0 / declared_total_states), 4)

    def to_dict(self) -> dict[str, Any]:
        return {
            "schema_version": 1,
            "states": [asdict(v) for v in sorted(self._states.values(), key=lambda s: s.state_id)],
            "transitions": [asdict(v) for v in sorted(self._transitions.values(), key=lambda t: t.transition_id)],
        }
