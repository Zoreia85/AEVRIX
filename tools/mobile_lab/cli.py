from __future__ import annotations

import argparse
import json
from pathlib import Path

from .algorithm_inference import NumericObservation, infer_numeric_rule
from .artifact_intelligence import inspect_artifact
from .scorecard import build_scorecard


def main() -> int:
    parser = argparse.ArgumentParser(prog="aevrix-mobile-lab")
    sub = parser.add_subparsers(dest="command", required=True)

    scan = sub.add_parser("inspect", help="Inventory an authorized mobile artifact without executing it")
    scan.add_argument("artifact", type=Path)
    scan.add_argument("--out", type=Path)

    sub.add_parser("scorecard-empty", help="Emit an explicitly unmeasured reconstruction scorecard")

    infer = sub.add_parser("infer-numeric", help="Evaluate narrow numeric rule families against black-box observations")
    infer.add_argument("dataset", type=Path, help="JSON array of {inputs: {name: number}, output: number}")
    infer.add_argument("--abs-tolerance", type=float, default=1e-9)
    infer.add_argument("--rel-tolerance", type=float, default=1e-9)
    infer.add_argument("--exhaustive-declared-domain", action="store_true")
    infer.add_argument("--out", type=Path)

    args = parser.parse_args()
    if args.command == "inspect":
        payload = inspect_artifact(args.artifact).to_dict()
    elif args.command == "infer-numeric":
        raw = json.loads(args.dataset.read_text(encoding="utf-8"))
        if not isinstance(raw, list):
            raise ValueError("dataset root must be a JSON array")
        observations = [NumericObservation(dict(item["inputs"]), item["output"]) for item in raw]
        payload = infer_numeric_rule(
            observations,
            abs_tolerance=args.abs_tolerance,
            rel_tolerance=args.rel_tolerance,
            exhaustive_declared_domain=args.exhaustive_declared_domain,
        ).to_dict()
    else:
        payload = build_scorecard().to_dict()

    rendered = json.dumps(payload, indent=2, sort_keys=True, ensure_ascii=False)
    if getattr(args, "out", None):
        args.out.write_text(rendered + "\n", encoding="utf-8")
    print(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
