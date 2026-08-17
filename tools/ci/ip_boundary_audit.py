#!/usr/bin/env python3
from __future__ import annotations

import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# Public implementation that was added after the public/private boundary decision.
# It is already disclosed under the public repository license. This gate does not
# attempt to restore secrecy; it freezes the disclosed implementation so materially
# new proprietary inference cannot silently evolve in the public shell.
DISCLOSED_FROZEN_BLOBS = {
    "tools/mobile_lab/algorithm_inference.py": "3b0e3a1fb863c38eb468c58d842d1dd5d47059a4",
}

FORBIDDEN_PRIVATE_SUFFIXES = {
    ".pdb", ".dmp", ".dump", ".gguf", ".onnx", ".safetensors", ".ckpt", ".pt", ".pth"
}
FORBIDDEN_PRIVATE_PARTS = {
    "black-core-source", "private-weights", "private-models", "project-memories"
}


def git_blob_sha(path: Path) -> str:
    result = subprocess.run(
        ["git", "hash-object", str(path)],
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip().lower()


def main() -> int:
    failures: list[str] = []

    for relative, expected_blob in DISCLOSED_FROZEN_BLOBS.items():
        path = ROOT / relative
        if not path.is_file():
            failures.append(
                f"disclosed public compatibility implementation disappeared without an explicit migration: {relative}"
            )
            continue
        actual = git_blob_sha(path)
        if actual != expected_blob:
            failures.append(
                f"disclosed crown-jewel implementation changed in public shell: {relative}; "
                "freeze it here and put materially new proprietary inference in AEVRIX-Black-Core"
            )

    for path in ROOT.rglob("*"):
        if not path.is_file() or ".git" in path.parts:
            continue
        relative = path.relative_to(ROOT).as_posix().lower()
        if path.suffix.lower() in FORBIDDEN_PRIVATE_SUFFIXES:
            failures.append(f"private/runtime artifact forbidden in public source repository: {relative}")
        if any(part in relative.split("/") for part in FORBIDDEN_PRIVATE_PARTS):
            failures.append(f"private Black Core material forbidden in public source repository: {relative}")

    if failures:
        print("IP BOUNDARY AUDIT: FAIL")
        for failure in failures:
            print(f"- {failure}")
        return 1

    print("IP BOUNDARY AUDIT: PASS")
    print(f"Frozen disclosed implementations: {len(DISCLOSED_FROZEN_BLOBS)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
