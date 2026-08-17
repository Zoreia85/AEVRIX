from __future__ import annotations

from .models import MetricResult, ReconstructionScorecard


def ratio_metric(name: str, numerator: int | None, denominator: int | None) -> MetricResult:
    if numerator is None or denominator is None or denominator <= 0:
        return MetricResult(name, numerator, denominator, None, "UNMEASURED")
    if numerator < 0 or numerator > denominator:
        raise ValueError(f"{name}: numerator must be within 0..denominator")
    percent = round(numerator * 100.0 / denominator, 4)
    return MetricResult(name, numerator, denominator, percent, "MEASURED")


def build_scorecard(
    *,
    states: tuple[int | None, int | None] = (None, None),
    flows: tuple[int | None, int | None] = (None, None),
    functions: tuple[int | None, int | None] = (None, None),
    algorithms: tuple[int | None, int | None] = (None, None),
    numerical_results: tuple[int | None, int | None] = (None, None),
    reports: tuple[int | None, int | None] = (None, None),
    integrations: tuple[int | None, int | None] = (None, None),
    critical_divergences_open: int | None = None,
) -> ReconstructionScorecard:
    metrics = (
        ratio_metric("state_coverage", *states),
        ratio_metric("flow_coverage", *flows),
        ratio_metric("function_coverage", *functions),
        ratio_metric("algorithm_coverage", *algorithms),
        ratio_metric("numerical_equivalence", *numerical_results),
        ratio_metric("report_equivalence", *reports),
        ratio_metric("integration_coverage", *integrations),
    )

    measured = [m for m in metrics if m.status == "MEASURED"]
    if critical_divergences_open is not None and critical_divergences_open < 0:
        raise ValueError("critical_divergences_open cannot be negative")

    if critical_divergences_open is None or not measured:
        status = "NOT_MEASURED"
    elif critical_divergences_open > 0:
        status = "NOT_HOMOLOGATED_CRITICAL_DIVERGENCE"
    elif len(measured) != len(metrics):
        status = "NOT_HOMOLOGATED_INCOMPLETE_EVIDENCE"
    else:
        status = "HOMOLOGATION_CANDIDATE"

    return ReconstructionScorecard(metrics, critical_divergences_open, status)
