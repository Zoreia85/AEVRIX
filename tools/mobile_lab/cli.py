from __future__ import annotations

import argparse
import json
from pathlib import Path

from .artifact_intelligence import inspect_artifact
from .scorecard import build_scorecard


def main() -> int:
    parser = argparse.ArgumentParser(prog="aevrix-mobile-lab")
    sub = parser.add_subparsers(dest="command", required=True)

    scan = sub.add_parser("inspect", help="Inventory an authorized mobile artifact without executing it")
    scan.add_argument("artifact", type=Path)
    scan.add_argument("--out", type=Path)

    sub.add_parser("scorecard-empty", help="Emit an explicitly unmeasured reconstruction scorecard")

    args = parser.parse_args()
    if args.command == "inspect":
        payload = inspect_artifact(args.artifact).to_dict()
    else:
        payload = build_scorecard().to_dict()

    rendered = json.dumps(payload, indent=2, sort_keys=True, ensure_ascii=False)
    if getattr(args, "out", None):
        args.out.write_text(rendered + "\n", encoding="utf-8")
    print(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
