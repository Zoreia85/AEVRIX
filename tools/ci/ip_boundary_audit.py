#!/usr/bin/env python3
from __future__ import annotations

import os
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# Public implementation disclosed before the public/private boundary decision.
# The byte identity is frozen: materially new proprietary inference belongs in
# AEVRIX-Black-Core when private execution capacity returns.
DISCLOSED_FROZEN_BLOBS = {
    "tools/mobile_lab/algorithm_inference.py": "3b0e3a1fb863c38eb468c58d842d1dd5d47059a4",
}

FORBIDDEN_PRIVATE_SUFFIXES = {
    ".pdb", ".dmp", ".dump", ".gguf", ".onnx", ".safetensors", ".ckpt", ".pt", ".pth"
}
FORBIDDEN_PRIVATE_PARTS = {
    "black-core-source", "private-weights", "private-models", "project-memories"
}

SOURCE_SUFFIXES = {".cs", ".py", ".ps1", ".cpp", ".cc", ".c", ".h", ".hpp", ".rs", ".go", ".java", ".kt"}
STRATEGIC_PATH_TOKENS = {
    "reasoning", "planner", "planning", "arbitration", "arbitrator", "reconstruction",
    "promotion", "scoring", "learning", "inference", "model-council", "modelcouncil",
    "provider-selection", "providerselection", "routing-heuristic", "routingheuristic",
}
STRATEGIC_CONTENT_MARKERS = {
    "reasoning engine", "planning engine", "provider arbitration", "provider selection policy",
    "reconstruction engine", "promotion score", "promotion scoring", "learning policy",
    "routing heuristic", "model council arbitration",
}
PUBLIC_SAFE_PREFIXES = (
    ".github/", "docs/", "tools/ci/", "tools/release/", "tools/qa/", "tests/",
)
PUBLIC_SAFE_PATH_PARTS = {"test", "tests", "contracts", "contract", "schemas", "schema", "interfaces", "interface"}


def run_git(*args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", *args], cwd=ROOT, check=check, capture_output=True, text=True
    )


def git_blob_sha(path: Path) -> str:
    return run_git("hash-object", str(path)).stdout.strip().lower()


def is_public_safe_path(relative: str) -> bool:
    normalized = relative.replace("\\", "/").lower()
    if normalized.startswith(PUBLIC_SAFE_PREFIXES):
        return True
    parts = set(Path(normalized).parts)
    if parts & PUBLIC_SAFE_PATH_PARTS:
        return True
    stem = Path(normalized).stem
    return any(token in stem for token in ("contract", "interface", "schema", "dto"))


def strategic_source_reason(relative: str, content: str = "") -> str | None:
    normalized = relative.replace("\\", "/").lower()
    suffix = Path(normalized).suffix.lower()
    if suffix not in SOURCE_SUFFIXES or is_public_safe_path(normalized):
        return None

    if any(token in normalized for token in STRATEGIC_PATH_TOKENS):
        return "strategic source path/name"

    lowered = content.lower()
    if any(marker in lowered for marker in STRATEGIC_CONTENT_MARKERS):
        return "strategic source content marker"
    return None


def policy_base_sha() -> str | None:
    raw = os.environ.get("AEVRIX_POLICY_BASE_SHA", "").strip().lower()
    if raw and raw != "0" * 40:
        result = run_git("cat-file", "-e", f"{raw}^{{commit}}", check=False)
        if result.returncode == 0:
            return raw
    fallback = run_git("rev-parse", "HEAD^", check=False)
    if fallback.returncode == 0:
        return fallback.stdout.strip().lower()
    return None


def changed_source_violations(base_sha: str | None) -> list[str]:
    if not base_sha:
        return ["cannot establish comparison base for public/private delta audit"]

    diff = run_git("diff", "--name-status", "--find-renames", base_sha, "HEAD").stdout.splitlines()
    failures: list[str] = []
    for line in diff:
        fields = line.split("\t")
        if len(fields) < 2:
            continue
        status = fields[0]
        if status.startswith("D"):
            continue
        relative = fields[-1].replace("\\", "/")
        path = ROOT / relative
        if not path.is_file():
            continue
        try:
            content = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            content = ""
        reason = strategic_source_reason(relative, content)
        if reason:
            failures.append(
                f"FAIL/IP_BOUNDARY: new or modified strategic source is not permitted in public mode: {relative} ({reason})"
            )
    return failures


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
                f"FAIL/IP_BOUNDARY: disclosed crown-jewel implementation changed in public shell: {relative}; "
                "freeze it here and put materially new proprietary inference in AEVRIX-Black-Core"
            )

    for path in ROOT.rglob("*"):
        if not path.is_file() or ".git" in path.parts:
            continue
        relative = path.relative_to(ROOT).as_posix().lower()
        if path.suffix.lower() in FORBIDDEN_PRIVATE_SUFFIXES:
            failures.append(f"FAIL/IP_BOUNDARY: private/runtime artifact forbidden in public source repository: {relative}")
        if any(part in relative.split("/") for part in FORBIDDEN_PRIVATE_PARTS):
            failures.append(f"FAIL/IP_BOUNDARY: private Black Core material forbidden in public source repository: {relative}")

    failures.extend(changed_source_violations(policy_base_sha()))

    if failures:
        print("IP BOUNDARY AUDIT: FAIL/IP_BOUNDARY")
        for failure in failures:
            print(f"- {failure}")
        return 1

    print("IP BOUNDARY AUDIT: PASS")
    print(f"Frozen disclosed implementations: {len(DISCLOSED_FROZEN_BLOBS)}")
    print("Strategic public-source delta: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
