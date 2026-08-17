from __future__ import annotations

import math
from dataclasses import asdict, dataclass
from typing import Iterable, Mapping, Sequence


@dataclass(frozen=True)
class NumericObservation:
    inputs: dict[str, float]
    output: float

    def __post_init__(self) -> None:
        if not self.inputs:
            raise ValueError("inputs cannot be empty")
        if not all(isinstance(k, str) and isinstance(v, (int, float)) for k, v in self.inputs.items()):
            raise ValueError("inputs must map string feature names to numeric values")
        if not isinstance(self.output, (int, float)):
            raise ValueError("output must be numeric")
        values = [float(v) for v in self.inputs.values()] + [float(self.output)]
        if not all(math.isfinite(v) for v in values):
            raise ValueError("observations must be finite")


@dataclass(frozen=True)
class CandidateModel:
    family: str
    features: tuple[str, ...]
    intercept: float
    coefficients: dict[str, float]
    parameter_count: int
    rmse: float
    max_abs_error: float
    accepted: bool

    def predict(self, inputs: Mapping[str, float]) -> float:
        return self.intercept + sum(self.coefficients[name] * float(inputs[name]) for name in self.features)

    def to_dict(self) -> dict:
        return asdict(self)


@dataclass(frozen=True)
class InferenceReport:
    status: str
    selected: CandidateModel | None
    candidates: tuple[CandidateModel, ...]
    observation_count: int
    exhaustive_declared_domain: bool
    ambiguity_count: int
    statement: str

    def to_dict(self) -> dict:
        return {
            "schema_version": 1,
            "status": self.status,
            "selected": None if self.selected is None else self.selected.to_dict(),
            "candidates": [c.to_dict() for c in self.candidates],
            "observation_count": self.observation_count,
            "exhaustive_declared_domain": self.exhaustive_declared_domain,
            "ambiguity_count": self.ambiguity_count,
            "statement": self.statement,
        }


def _solve_linear(matrix: list[list[float]], vector: list[float], eps: float = 1e-12) -> list[float] | None:
    n = len(vector)
    augmented = [row[:] + [vector[i]] for i, row in enumerate(matrix)]
    for col in range(n):
        pivot = max(range(col, n), key=lambda r: abs(augmented[r][col]))
        if abs(augmented[pivot][col]) <= eps:
            return None
        augmented[col], augmented[pivot] = augmented[pivot], augmented[col]
        scale = augmented[col][col]
        augmented[col] = [value / scale for value in augmented[col]]
        for row in range(n):
            if row == col:
                continue
            factor = augmented[row][col]
            if abs(factor) <= eps:
                continue
            augmented[row] = [a - factor * b for a, b in zip(augmented[row], augmented[col])]
    return [augmented[i][-1] for i in range(n)]


def _least_squares(design: Sequence[Sequence[float]], outputs: Sequence[float]) -> list[float] | None:
    if not design:
        return None
    columns = len(design[0])
    if any(len(row) != columns for row in design):
        raise ValueError("design rows have inconsistent widths")
    gram = [[0.0 for _ in range(columns)] for _ in range(columns)]
    rhs = [0.0 for _ in range(columns)]
    for row, y in zip(design, outputs):
        for i in range(columns):
            rhs[i] += row[i] * y
            for j in range(columns):
                gram[i][j] += row[i] * row[j]
    return _solve_linear(gram, rhs)


def _model(
    family: str,
    features: tuple[str, ...],
    intercept: float,
    coefficients: dict[str, float],
    observations: Sequence[NumericObservation],
    threshold: float,
) -> CandidateModel:
    errors = []
    for obs in observations:
        prediction = intercept + sum(coefficients[name] * float(obs.inputs[name]) for name in features)
        errors.append(prediction - float(obs.output))
    rmse = math.sqrt(sum(error * error for error in errors) / len(errors))
    max_error = max(abs(error) for error in errors)
    parameter_count = len(coefficients) + (1 if family != "proportional" else 0)
    return CandidateModel(
        family=family,
        features=features,
        intercept=intercept,
        coefficients=coefficients,
        parameter_count=parameter_count,
        rmse=rmse,
        max_abs_error=max_error,
        accepted=max_error <= threshold,
    )


def infer_numeric_rule(
    observations: Iterable[NumericObservation],
    *,
    abs_tolerance: float = 1e-9,
    rel_tolerance: float = 1e-9,
    exhaustive_declared_domain: bool = False,
) -> InferenceReport:
    obs = tuple(observations)
    if not obs:
        return InferenceReport(
            status="UNEXPLAINED",
            selected=None,
            candidates=(),
            observation_count=0,
            exhaustive_declared_domain=exhaustive_declared_domain,
            ambiguity_count=0,
            statement="No observations were supplied; no rule can be inferred.",
        )
    features = tuple(sorted(obs[0].inputs))
    if any(tuple(sorted(item.inputs)) != features for item in obs):
        raise ValueError("all observations must use the same feature set")
    if abs_tolerance < 0 or rel_tolerance < 0:
        raise ValueError("tolerances cannot be negative")
    output_scale = max(1.0, max(abs(float(item.output)) for item in obs))
    threshold = abs_tolerance + rel_tolerance * output_scale
    outputs = [float(item.output) for item in obs]

    candidates: list[CandidateModel] = []
    mean = sum(outputs) / len(outputs)
    candidates.append(_model("constant", features, mean, {name: 0.0 for name in features}, obs, threshold))

    if len(features) == 1:
        feature = features[0]
        denominator = sum(float(item.inputs[feature]) ** 2 for item in obs)
        if denominator > 1e-15:
            coefficient = sum(float(item.inputs[feature]) * float(item.output) for item in obs) / denominator
            candidates.append(_model("proportional", features, 0.0, {feature: coefficient}, obs, threshold))

    design = [[1.0] + [float(item.inputs[name]) for name in features] for item in obs]
    solution = _least_squares(design, outputs)
    if solution is not None:
        candidates.append(
            _model(
                "affine" if len(features) == 1 else "multivariate_affine",
                features,
                solution[0],
                {name: solution[index + 1] for index, name in enumerate(features)},
                obs,
                threshold,
            )
        )

    accepted = [candidate for candidate in candidates if candidate.accepted]
    if not accepted:
        ordered = tuple(sorted(candidates, key=lambda c: (c.max_abs_error, c.parameter_count, c.family)))
        return InferenceReport(
            status="UNEXPLAINED",
            selected=None,
            candidates=ordered,
            observation_count=len(obs),
            exhaustive_declared_domain=exhaustive_declared_domain,
            ambiguity_count=0,
            statement="No tested rule family explains all observations within tolerance.",
        )

    accepted.sort(key=lambda c: (c.parameter_count, c.max_abs_error, c.family))
    selected = accepted[0]
    ambiguity_count = len(accepted)

    if exhaustive_declared_domain and ambiguity_count == 1 and selected.max_abs_error <= threshold:
        status = "PROVEN_WITHIN_DECLARED_DOMAIN"
        statement = "The selected rule matches every case in the explicitly declared exhaustive finite domain."
    elif ambiguity_count > 1:
        status = "INFERRED_AMBIGUOUS"
        statement = "Multiple rule families fit the current observations; discriminating tests are required."
    elif len(obs) >= max(5, selected.parameter_count * 3):
        status = "HIGHLY_PROBABLE"
        statement = "One tested rule family uniquely fits the current observations, but finite black-box evidence is not a proof."
    else:
        status = "INFERRED"
        statement = "The selected rule is consistent with limited observations; more tests are required."

    ordered = tuple(sorted(candidates, key=lambda c: (not c.accepted, c.parameter_count, c.max_abs_error, c.family)))
    return InferenceReport(
        status=status,
        selected=selected,
        candidates=ordered,
        observation_count=len(obs),
        exhaustive_declared_domain=exhaustive_declared_domain,
        ambiguity_count=ambiguity_count,
        statement=statement,
    )


def select_discriminating_case(
    report: InferenceReport,
    candidate_inputs: Iterable[Mapping[str, float]],
) -> dict[str, float] | None:
    accepted = [candidate for candidate in report.candidates if candidate.accepted]
    if len(accepted) < 2:
        return None
    best: dict[str, float] | None = None
    best_spread = -1.0
    for raw in candidate_inputs:
        case = {name: float(value) for name, value in raw.items()}
        try:
            predictions = [candidate.predict(case) for candidate in accepted]
        except KeyError:
            continue
        spread = max(predictions) - min(predictions)
        if spread > best_spread:
            best_spread = spread
            best = case
    return best if best_spread > 0.0 else None
