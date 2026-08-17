#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import re
from pathlib import Path

HEX64 = re.compile(r"^[0-9a-fA-F]{64}$")

_FIRST_RUN_MODULE_PATH = Path(__file__).with_name("first_run_evidence.py")
_FIRST_RUN_SPEC = importlib.util.spec_from_file_location("aevrix_first_run_evidence", _FIRST_RUN_MODULE_PATH)
if _FIRST_RUN_SPEC is None or _FIRST_RUN_SPEC.loader is None:
    raise RuntimeError("unable to load first-run evidence validator")
_FIRST_RUN_MODULE = importlib.util.module_from_spec(_FIRST_RUN_SPEC)
_FIRST_RUN_SPEC.loader.exec_module(_FIRST_RUN_MODULE)
validate_first_run_evidence = _FIRST_RUN_MODULE.validate_first_run_evidence


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


def validate_first_run_bundle(candidate: dict, first_run_path: Path, expected_commit: str) -> tuple[str, dict]:
    require(first_run_path.is_file(), "first-run evidence was declared/supplied but file is missing")
    declared_hash = str(candidate.get("firstRunTermsEvidenceSha256") or "").lower()
    require(bool(HEX64.fullmatch(declared_hash)), "candidate evidence does not declare a valid firstRunTermsEvidenceSha256")
    actual_hash = sha256_file(first_run_path)
    require(declared_hash == actual_hash, "first-run evidence SHA-256 does not match candidate declaration")

    first_run = load_json(first_run_path)
    expected_version = str(candidate.get("newVersion") or "")
    require(bool(expected_version), "candidate newVersion is missing before first-run validation")
    details = validate_first_run_evidence(first_run, expected_commit, expected_version)

    embedded = candidate.get("firstRunTerms")
    require(isinstance(embedded, dict), "candidate evidence does not embed firstRunTerms")
    for field in (
        "schemaVersion",
        "candidateSha",
        "productVersion",
        "termsRevision",
        "installExitCode",
        "uninstallExitCode",
        "presentationObserved",
        "presentedAtUtc",
        "preAcceptanceNavigationAbsent",
        "declineExitedWithoutAcceptance",
        "initialAcceptDisabled",
        "explicitConfirmationRequired",
        "acceptancePersisted",
        "acceptedAtUtc",
        "commandCenterTransitionObserved",
        "secondLaunchSkippedFirstRun",
        "acceptanceSurvivedUninstall",
    ):
        require(embedded.get(field) == first_run.get(field), f"candidate embedded firstRunTerms mismatch for {field}")

    return actual_hash, details


def validate_defender_evidence(candidate: dict, defender: dict, expected_commit: str) -> tuple[str, dict]:
    require(defender.get("candidateSha") == expected_commit, "Defender candidateSha does not match exact readiness candidate")
    declared = str(candidate.get("defenderEvidenceSha256") or "").lower()
    require(bool(HEX64.fullmatch(declared)), "candidate evidence does not declare a valid defenderEvidenceSha256")

    status = str(defender.get("status") or "")
    require(status in {"PASS", "FAIL", "INFRASTRUCTURE_INCONCLUSIVE"}, f"invalid Defender status: {status!r}")

    hashes = candidate.get("installerHashes") or {}
    expected_hashes = {
        str(hashes.get("oldSha256") or "").lower(),
        str(hashes.get("newSha256") or "").lower(),
    }
    require(all(HEX64.fullmatch(value) for value in expected_hashes), "installer hashes are missing before Defender validation")

    before = defender.get("artifactHashesBeforeScan") or {}
    after = defender.get("artifactHashesAfterScan") or {}
    before_values = {str(value or "").lower() for value in before.values()}
    after_values = {str(value or "").lower() for value in after.values()}
    require(expected_hashes == before_values, "Defender pre-scan hashes do not match exact installer hashes")
    require(expected_hashes == after_values, "Defender post-scan hashes do not match exact installer hashes")

    detections = int(defender.get("matchingDetectionCount") or 0)
    if status == "PASS":
        require(detections == 0, "Defender PASS cannot include path-bound detections")
        require(defender.get("antivirusEnabled") is True, "Defender PASS requires antivirusEnabled=true")
        require(defender.get("amServiceEnabled") is True, "Defender PASS requires AMServiceEnabled=true")

    details = {
        "status": status,
        "reason": defender.get("reason"),
        "engineVersion": defender.get("engineVersion"),
        "productVersion": defender.get("productVersion"),
        "signatureVersion": defender.get("signatureVersion"),
        "signatureLastUpdated": defender.get("signatureLastUpdated"),
        "matchingDetectionCount": detections,
        "artifactHashesBeforeScan": before,
        "artifactHashesAfterScan": after,
    }
    return status, details


def main() -> int:
    parser = argparse.ArgumentParser(description="Convert exact Windows installer AVA evidence into fail-closed release-gate evidence")
    parser.add_argument("--candidate-evidence", required=True)
    parser.add_argument("--lifecycle-evidence", required=True)
    parser.add_argument("--first-run-evidence")
    parser.add_argument("--defender-evidence")
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

    first_run_path: Path | None = None
    if args.first_run_evidence:
        first_run_path = Path(args.first_run_evidence)
    else:
        declared_first_run_hash = str(candidate.get("firstRunTermsEvidenceSha256") or "").lower()
        first_run_file = str(candidate.get("firstRunTermsEvidenceFile") or "").strip()
        if declared_first_run_hash or first_run_file:
            require(bool(declared_first_run_hash and first_run_file), "candidate first-run evidence declaration is incomplete")
            first_run_path = candidate_path.parent / first_run_file

    if first_run_path is None:
        installer_gate = {
            "status": "PARCIAL",
            "evidenceRef": args.evidence_ref,
            "evidenceSha256": candidate_hash,
            "reason": "Exact installer lifecycle is proven; mandatory terms/first-run evidence is absent and prevents full PASS.",
            "lifecycleEvidenceSha256": actual_lifecycle_hash,
            "details": details,
        }
    else:
        first_run_hash, first_run_details = validate_first_run_bundle(candidate, first_run_path, args.expected_source_commit)
        details["firstRunTerms"] = first_run_details
        installer_gate = {
            "status": "PASS",
            "evidenceRef": args.evidence_ref,
            "evidenceSha256": candidate_hash,
            "artifactSha256": details["newInstallerSha256"],
            "reason": "Exact canonical installer lifecycle and mandatory installed first-run/terms behavior are physically proven on the same candidate.",
            "lifecycleEvidenceSha256": actual_lifecycle_hash,
            "firstRunTermsEvidenceSha256": first_run_hash,
            "details": details,
        }

    gates: dict[str, dict] = {"installer-lifecycle": installer_gate}

    defender_path: Path | None = None
    if args.defender_evidence:
        defender_path = Path(args.defender_evidence)
    else:
        declared_defender_hash = str(candidate.get("defenderEvidenceSha256") or "").lower()
        defender_file = str(candidate.get("defenderEvidenceFile") or "").strip()
        if declared_defender_hash and defender_file:
            defender_path = candidate_path.parent / defender_file

    if defender_path is not None:
        require(defender_path.is_file(), "Defender evidence was declared/supplied but file is missing")
        actual_defender_hash = sha256_file(defender_path)
        declared_defender_hash = str(candidate.get("defenderEvidenceSha256") or "").lower()
        require(declared_defender_hash == actual_defender_hash, "Defender evidence SHA-256 does not match candidate declaration")
        defender = load_json(defender_path)
        defender_status, defender_details = validate_defender_evidence(candidate, defender, args.expected_source_commit)

        if defender_status == "PASS":
            distribution_status = "PARCIAL"
            reason = "Exact installer artifacts passed Microsoft Defender with stable hashes; Authenticode and signed update/tamper controls remain mandatory."
        elif defender_status == "FAIL":
            distribution_status = "FAIL"
            reason = "Microsoft Defender evidence failed for exact installer artifacts."
        else:
            distribution_status = "INFRASTRUCTURE_INCONCLUSIVE"
            reason = "Microsoft Defender was unavailable or inconclusive on this validation runner; no distribution-security credit is granted."

        gates["distribution-security"] = {
            "status": distribution_status,
            "evidenceRef": args.evidence_ref,
            "evidenceSha256": actual_defender_hash,
            "reason": reason,
            "defenderEvidenceSha256": actual_defender_hash,
            "details": defender_details,
        }

    payload = {
        "schemaVersion": 2,
        "sourceCommit": args.expected_source_commit,
        "gates": gates,
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(payload, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
