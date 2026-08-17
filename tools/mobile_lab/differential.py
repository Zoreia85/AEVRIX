from __future__ import annotations

import math
from dataclasses import asdict, dataclass
from typing import Any


@dataclass(frozen=True)
class NumericDifferential:
    original: float
    reconstruction: float
    abs_error: float
    rel_error: float
    abs_tolerance: float
    rel_tolerance: float
    equivalent: bool

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def compare_numeric(
    original: float,
    reconstruction: float,
    *,
    abs_tolerance: float = 1e-9,
    rel_tolerance: float = 1e-9,
) -> NumericDifferential:
    values = (float(original), float(reconstruction), abs_tolerance, rel_tolerance)
    if not all(math.isfinite(v) for v in values):
        raise ValueError("values and tolerances must be finite")
    if abs_tolerance < 0 or rel_tolerance < 0:
        raise ValueError("tolerances cannot be negative")
    abs_error = abs(float(original) - float(reconstruction))
    scale = max(abs(float(original)), abs(float(reconstruction)), 1e-300)
    rel_error = abs_error / scale
    equivalent = abs_error <= max(abs_tolerance, rel_tolerance * scale)
    return NumericDifferential(
        original=float(original),
        reconstruction=float(reconstruction),
        abs_error=abs_error,
        rel_error=rel_error,
        abs_tolerance=abs_tolerance,
        rel_tolerance=rel_tolerance,
        equivalent=equivalent,
    )
