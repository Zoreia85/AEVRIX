#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

HEX64 = re.compile(r"^[0-9a-fA-F]{64}$")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def validate_installer_evidence(candidate: dict, lifecycle: dict, expected_commit: str) -> dict:
    require(candidate.get("candidateSha") == expected_commit, "installer candidateSha does not match exact readiness candidate")
    require(candidate.get("canonicalPromotion") == "BOT_AUTHORED_EXACT_CANDIDATE", "installer evidence is not from canonical bot-authored promotion")
    require(candidate.get("aevrixAuthenticodeGate") == "BLOCKED_UNSIGNED_CANDIDATE", "unexpected installer Authenticode gate state")

    hashes = candidate.get("installerHashes") or {}
    old_hash = str(hashes.get("oldSha256") or "")
    new_hash = str(hashes.get("newSha256") or "")
    require(bool(HEX64.fullmatch(old_hash)), "old installer SHA-256 missing or invalid")
    require(bool(HEX64.fullmatch(new_hash)), "new installer SHA-256 missing or invalid")

    require(candidate.get("oldVersion") == lifecycle.get("oldVersion"), "old version mismatch between candidate and lifecycle evidence")
    require(candidate.get("newVersion") == lifecycle.get("newVersion"), "new version mismatch between candidate and lifecycle evidence")

    interruption = lifecycle.get("interruption") or {}
    require(interruption.get("observed") is True, "controlled installer interruption was not observed")
    require(interruption.get("partialSurfacePresent") is True, "controlled interruption did not leave recoverable product surface")
    require(interruption.get("recoverySucceeded") is True, "interrupted install recovery did not succeed")

    phase = lifecycle.get("phaseExitCodes") or {}
    for name in ("recoveryInstall", "repair", "upgrade", "uninstall"):
        require(phase.get(name) == 0, f"installer lifecycle phase {name} did not exit 0")
    require(phase.get("downgradeAttempt") == 1638, "downgrade attempt was not rejected with expected code 1638")
    require("interruptedInstall" in phase, "interrupted install exit code was not recorded")

    executable_hashes = lifecycle.get("installedExecutableHashes") or {}
    desktop_hash = str(executable_hashes.get("desktopSha256") or "")
    engine_hash = str(executable_hashes.get("engineHostSha256") or "")
    require(bool(HEX64.fullmatch(desktop_hash)), "installed Desktop SHA-256 missing or invalid")
    require(bool(HEX64.fullmatch(engine_hash)), "installed EngineHost SHA-256 missing or invalid")

    require(lifecycle.get("residueVerdict") == "PASS_NO_PRODUCT_OWNED_RESIDUE", "product-owned residue cleanup is not PASS")
    require(lifecycle.get("userDataPreservation") == "PASS", "user-data preservation is not PASS")

    return {
        "oldInstallerSha256": old_hash.lower(),
        "newInstallerSha256": new_hash.lower(),
        "installedDesktopSha256": desktop_hash.lower(),
        "installedEngineHostSha256": engine_hash.lower(),
        "phaseExitCodes": phase,
        "interruption": interruption,
        "residueVerdict": lifecycle.get("residueVerdict"),
        "userDataPreservation": lifecycle.get("userDataPreservation"),
        "authenticodeGate": candidate.get("aevrixAuthenticodeGate"),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Convert exact Windows installer AVA evidence into fail-closed release-gate evidence")
    parser.add_argument("--candidate-evidence", required=True)
    parser.add_argument("--lifecycle-evidence", required=True)
    parser.add_argument("--expected-source-commit", required=True)
    parser.add_argument("--evidence-ref", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    candidate_path = Path(args.candidate_evidence)
    lifecycle_path = Path(args.lifecycle_evidence)
    output_path = Path(args.output)

    candidate = load_json(candidate_path)
    lifecycle = load_json(lifecycle_path)

    declared_lifecycle_hash = str(candidate.get("lifecycleEvidenceSha256") or "").lower()
    actual_lifecycle_hash = sha256_file(lifecycle_path)
    require(bool(HEX64.fullmatch(declared_lifecycle_hash)), "candidate evidence does not declare a valid lifecycleEvidenceSha256")
    require(declared_lifecycle_hash == actual_lifecycle_hash, "lifecycle evidence SHA-256 does not match candidate declaration")

    details = validate_installer_evidence(candidate, lifecycle, args.expected_source_commit)
    candidate_hash = sha256_file(candidate_path)

    payload = {
        "schemaVersion": 1,
        "sourceCommit": args.expected_source_commit,
        "gates": {
            "installer-lifecycle": {
                "status": "PARCIAL",
                "evidenceRef": args.evidence_ref,
                "evidenceSha256": candidate_hash,
                "reason": "Exact installer lifecycle is proven; mandatory terms/first-run remains outside this lifecycle artifact and prevents full PASS.",
                "lifecycleEvidenceSha256": actual_lifecycle_hash,
                "details": details,
            }
        },
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(payload, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
