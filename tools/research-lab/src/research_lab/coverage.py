from __future__ import annotations

from dataclasses import dataclass
from enum import StrEnum


class CoverageStatus(StrEnum):
    COMPLETE = "complete"
    PARTIAL = "partial"
    BLOCKED = "blocked"


@dataclass(frozen=True, slots=True)
class CoverageSnapshot:
    discovered_states: int
    visited_states: int
    queued_states: int = 0
    visiting_states: int = 0
    blocked_states: int = 0
    error_states: int = 0
    discovered_routes: int = 0
    visited_routes: int = 0
    discovered_endpoints: int = 0
    inspected_endpoints: int = 0
    pagination_open: int = 0
    unresolved_session_interruptions: int = 0
    inaccessible_areas: int = 0

    def __post_init__(self) -> None:
        values = self.__dict__.values() if hasattr(self, "__dict__") else (
            self.discovered_states,
            self.visited_states,
            self.queued_states,
            self.visiting_states,
            self.blocked_states,
            self.error_states,
            self.discovered_routes,
            self.visited_routes,
            self.discovered_endpoints,
            self.inspected_endpoints,
            self.pagination_open,
            self.unresolved_session_interruptions,
            self.inaccessible_areas,
        )
        if any(value < 0 for value in values):
            raise ValueError("coverage counters cannot be negative")
        if self.visited_states > self.discovered_states:
            raise ValueError("visited_states cannot exceed discovered_states")
        if self.visited_routes > self.discovered_routes:
            raise ValueError("visited_routes cannot exceed discovered_routes")
        if self.inspected_endpoints > self.discovered_endpoints:
            raise ValueError("inspected_endpoints cannot exceed discovered_endpoints")

    @property
    def structural_percent(self) -> float:
        dimensions: list[tuple[int, int]] = []
        if self.discovered_states:
            dimensions.append((self.visited_states, self.discovered_states))
        if self.discovered_routes:
            dimensions.append((self.visited_routes, self.discovered_routes))
        if self.discovered_endpoints:
            dimensions.append((self.inspected_endpoints, self.discovered_endpoints))
        if not dimensions:
            return 0.0
        numerator = sum(done / total for done, total in dimensions)
        return round((numerator / len(dimensions)) * 100, 2)

    @property
    def status(self) -> CoverageStatus:
        if self.inaccessible_areas > 0 and self.visited_states == 0:
            return CoverageStatus.BLOCKED
        pending = (
            self.queued_states
            + self.visiting_states
            + self.error_states
            + self.pagination_open
            + self.unresolved_session_interruptions
        )
        exhaustive = (
            self.discovered_states > 0
            and self.visited_states == self.discovered_states
            and self.visited_routes == self.discovered_routes
            and self.inspected_endpoints == self.discovered_endpoints
        )
        if exhaustive and pending == 0:
            return CoverageStatus.COMPLETE
        return CoverageStatus.PARTIAL

    def report(self) -> dict[str, object]:
        return {
            "status": self.status.value,
            "structuralPercent": self.structural_percent,
            "states": {
                "discovered": self.discovered_states,
                "visited": self.visited_states,
                "queued": self.queued_states,
                "visiting": self.visiting_states,
                "blocked": self.blocked_states,
                "errors": self.error_states,
            },
            "routes": {"discovered": self.discovered_routes, "visited": self.visited_routes},
            "endpoints": {
                "discovered": self.discovered_endpoints,
                "inspected": self.inspected_endpoints,
            },
            "paginationOpen": self.pagination_open,
            "unresolvedSessionInterruptions": self.unresolved_session_interruptions,
            "inaccessibleAreas": self.inaccessible_areas,
        }
