from __future__ import annotations

import hashlib
import json
import statistics
from dataclasses import dataclass
from typing import Sequence


@dataclass(frozen=True, order=True)
class FindingKey:
    category: str
    subject: str
    detail: str = ""

    def canonical(self) -> str:
        return "|".join(part.strip().lower() for part in (self.category, self.subject, self.detail))


@dataclass(frozen=True)
class BenchmarkCase:
    case_id: str
    artifact_sha256: str
    expected_findings: frozenset[FindingKey] | None

    @property
    def truth_status(self) -> str:
        return "KNOWN" if self.expected_findings is not None else "UNKNOWN"


@dataclass(frozen=True)
class ToolCaseResult:
    capability_id: str
    case_id: str
    status: str
    findings: frozenset[FindingKey]
    duration_ms: float
    evidence_hashes: tuple[str, ...] = ()

    def validate(self) -> "ToolCaseResult":
        if self.status not in {"SUCCESS", "FAILED", "TIMEOUT", "UNAVAILABLE"}:
            raise ValueError("unsupported tool result status")
        if self.duration_ms < 0:
            raise ValueError("duration_ms cannot be negative")
        return self


@dataclass(frozen=True)
class ToolBenchmarkSummary:
    capability_id: str
    attempts: int
    successes: int
    reliability: float
    accuracy_status: str
    truth_cases: int
    expected_findings: int | None
    observed_findings: int
    true_positives: int | None
    false_positives: int | None
    false_negatives: int | None
    recall: float | None
    precision: float | None
    f1: float | None
    median_duration_ms: float | None
    unique_observed_signals: int

    def to_dict(self) -> dict:
        return self.__dict__.copy()


@dataclass(frozen=True)
class BenchmarkReport:
    summaries: tuple[ToolBenchmarkSummary, ...]
    disagreement_cases: tuple[dict, ...]

    def to_dict(self) -> dict:
        return {
            "schema_version": 1,
            "summaries": [s.to_dict() for s in self.summaries],
            "disagreement_cases": list(self.disagreement_cases),
        }

    @property
    def sha256(self) -> str:
        payload = json.dumps(self.to_dict(), sort_keys=True, separators=(",", ":")).encode()
        return hashlib.sha256(payload).hexdigest()


class BenchmarkEvaluator:
    """Produces measured facts for central capability governance; never lifecycle scores."""

    @staticmethod
    def evaluate(cases: Sequence[BenchmarkCase], results: Sequence[ToolCaseResult]) -> BenchmarkReport:
        case_map = {case.case_id: case for case in cases}
        if len(case_map) != len(cases):
            raise ValueError("benchmark case ids must be unique")
        validated = [r.validate() for r in results]
        unknown_cases = sorted({r.case_id for r in validated if r.case_id not in case_map})
        if unknown_cases:
            raise ValueError(f"results reference unknown cases: {unknown_cases}")

        tools = sorted({r.capability_id for r in validated})
        all_by_case: dict[str, dict[str, frozenset[FindingKey]]] = {}
        for r in validated:
            if r.status == "SUCCESS":
                all_by_case.setdefault(r.case_id, {})[r.capability_id] = r.findings

        summaries: list[ToolBenchmarkSummary] = []
        for tool in tools:
            rows = [r for r in validated if r.capability_id == tool]
            successes = [r for r in rows if r.status == "SUCCESS"]
            truth_rows = [r for r in successes if case_map[r.case_id].expected_findings is not None]

            tp = fp = fn = 0
            if truth_rows:
                for row in truth_rows:
                    expected = case_map[row.case_id].expected_findings or frozenset()
                    tp += len(row.findings & expected)
                    fp += len(row.findings - expected)
                    fn += len(expected - row.findings)
                recall = tp / (tp + fn) if tp + fn else 1.0
                precision = tp / (tp + fp) if tp + fp else 1.0
                f1 = 0.0 if recall + precision == 0 else 2 * recall * precision / (recall + precision)
                accuracy_status = "MEASURED"
                expected_total: int | None = tp + fn
            else:
                tp = fp = fn = None
                recall = precision = f1 = None
                accuracy_status = "UNMEASURED"
                expected_total = None

            observed_union = set().union(*(r.findings for r in successes)) if successes else set()
            peers_union: set[FindingKey] = set()
            for tool_map in all_by_case.values():
                for peer, findings in tool_map.items():
                    if peer != tool:
                        peers_union.update(findings)
            unique = len(observed_union - peers_union)

            durations = [r.duration_ms for r in successes]
            summaries.append(ToolBenchmarkSummary(
                capability_id=tool,
                attempts=len(rows),
                successes=len(successes),
                reliability=(len(successes) / len(rows)) if rows else 0.0,
                accuracy_status=accuracy_status,
                truth_cases=len(truth_rows),
                expected_findings=expected_total,
                observed_findings=len(observed_union),
                true_positives=tp,
                false_positives=fp,
                false_negatives=fn,
                recall=recall,
                precision=precision,
                f1=f1,
                median_duration_ms=statistics.median(durations) if durations else None,
                unique_observed_signals=unique,
            ))

        disagreements: list[dict] = []
        for case_id, tool_map in sorted(all_by_case.items()):
            if len(tool_map) < 2:
                continue
            normalized = {tool: sorted(f.canonical() for f in findings) for tool, findings in tool_map.items()}
            signatures = {tuple(values) for values in normalized.values()}
            if len(signatures) > 1:
                disagreements.append({"case_id": case_id, "tool_findings": normalized})

        return BenchmarkReport(tuple(summaries), tuple(disagreements))
