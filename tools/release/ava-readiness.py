#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET

PASS = "PASS"
FAIL = "FAIL"
PARTIAL = "PARCIAL"
BLOCKED = "BLOQUEADO"
NOT_RUN = "NOT_RUN"
INCONCLUSIVE = "INFRASTRUCTURE_INCONCLUSIVE"
ALLOWED_STATUSES = {PASS, FAIL, PARTIAL, BLOCKED, NOT_RUN, INCONCLUSIVE}
HEX64 = re.compile(r"^[0-9a-fA-F]{64}$")
EXTERNAL_GATE_IDS = {
    "windows-e2e-runtime",
    "installer-lifecycle",
    "distribution-security",
    "execution-authority-db",
    "performance-stability",
    "ux-accessibility",
}


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def validate_model(model: dict) -> list[str]:
    errors: list[str] = []
    gates = model.get("gates") or []
    if not gates:
        errors.append("readiness model contains no gates")
        return errors

    weights = 0
    points = 0
    seen: set[str] = set()
    for gate in gates:
        gate_id = str(gate.get("id") or "")
        if not gate_id or gate_id in seen:
            errors.append(f"invalid or duplicate gate id: {gate_id!r}")
        seen.add(gate_id)
        weight = int(gate.get("weight", -1))
        gate_points = int(gate.get("points", -1))
        status = gate.get("status")
        if weight < 0 or gate_points < 0 or gate_points > weight:
            errors.append(f"gate {gate_id}: invalid weight/points {gate_points}/{weight}")
        if status not in ALLOWED_STATUSES:
            errors.append(f"gate {gate_id}: invalid status {status!r}")
        weights += weight
        points += gate_points

    required_weight = int((model.get("rules") or {}).get("weightsMustTotal", 100))
    if weights != required_weight:
        errors.append(f"weights total {weights}, expected {required_weight}")
    if points != int(model.get("readinessPercent", -1)):
        errors.append(f"points total {points}, readinessPercent is {model.get('readinessPercent')}")

    decision = model.get("releaseDecision")
    if decision == "HOMOLOGATED":
        if points != 100:
            errors.append("HOMOLOGATED requires readinessPercent=100")
        non_pass = [gate.get("id") for gate in gates if gate.get("status") != PASS]
        if non_pass:
            errors.append("HOMOLOGATED requires all gates PASS: " + ", ".join(map(str, non_pass)))
    if points == 100 and any(gate.get("status") != PASS for gate in gates):
        errors.append("100% is forbidden while any mandatory gate is not PASS")
    return errors


def parse_trx(path: Path) -> dict:
    if not path.is_file():
        return {"status": BLOCKED, "reason": "TRX file missing", "path": str(path)}
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as exc:
        return {"status": INCONCLUSIVE, "reason": f"TRX parse failed: {exc}", "path": str(path)}

    counters = None
    for node in root.iter():
        if node.tag.endswith("Counters"):
            counters = node.attrib
            break
    if counters is None:
        return {"status": INCONCLUSIVE, "reason": "TRX counters missing", "path": str(path)}

    def n(name: str) -> int:
        try:
            return int(counters.get(name, "0"))
        except ValueError:
            return 0

    total = n("total")
    passed = n("passed")
    failed = (
        n("failed") + n("error") + n("timeout") + n("aborted") + n("inconclusive")
        + n("passedButRunAborted") + n("warning")
    )
    not_executed = (
        n("notExecuted") + n("notRunnable") + n("disconnected") + n("pending") + n("inProgress")
    )
    status = PASS if total > 0 and failed == 0 and not_executed == 0 and passed == total else FAIL
    return {
        "status": status,
        "total": total,
        "passed": passed,
        "failed": failed,
        "notExecuted": not_executed,
        "sha256": sha256_file(path),
        "file": path.name,
    }


def artifact_build_evidence(directory: Path, outcome: str, required_filename: str) -> tuple[dict, list[dict]]:
    files: list[dict] = []
    if directory.is_dir():
        for path in sorted(directory.rglob("*")):
            if path.is_file():
                files.append({
                    "file": path.relative_to(directory).as_posix(),
                    "sizeBytes": path.stat().st_size,
                    "sha256": sha256_file(path),
                })
    required_present = any(Path(entry["file"]).name.lower() == required_filename.lower() for entry in files)
    status = PASS if outcome == "success" and files and required_present else FAIL
    return ({
        "status": status,
        "outcome": outcome,
        "artifactCount": len(files),
        "requiredFile": required_filename,
        "requiredFilePresent": required_present,
    }, files)


def parse_soak(path: Path, expected_engine_hashes: set[str]) -> dict:
    if not path.is_file():
        return {"status": BLOCKED, "reason": "EngineHost soak report missing", "path": str(path)}
    try:
        payload = load_json(path)
    except (OSError, json.JSONDecodeError) as exc:
        return {"status": INCONCLUSIVE, "reason": f"soak report parse failed: {exc}", "path": str(path)}

    report_hash = sha256_file(path)
    engine_hash = str(payload.get("engineHostSha256") or "").lower()
    requested = int(payload.get("requestedIterations") or 0)
    completed = int(payload.get("completedIterations") or 0)
    failures = payload.get("failures") or []
    declared_pass = payload.get("pass") is True
    hash_bound = bool(engine_hash) and engine_hash in {value.lower() for value in expected_engine_hashes}

    status = PASS if declared_pass and requested > 0 and completed == requested and not failures and hash_bound else FAIL
    return {
        "status": status,
        "reportSha256": report_hash,
        "engineHostSha256": engine_hash,
        "engineHashBoundToPublishedCandidate": hash_bound,
        "requestedIterations": requested,
        "completedIterations": completed,
        "restartCount": payload.get("restartCount"),
        "durationMilliseconds": payload.get("durationMilliseconds"),
        "latencyMilliseconds": payload.get("latencyMilliseconds"),
        "resources": payload.get("resources"),
        "failures": failures,
    }


def discover_desktop_surface(repo_root: Path) -> dict:
    src = repo_root / "apps" / "aevrix-windows" / "src"
    if not src.is_dir():
        return {"status": BLOCKED, "reason": "Windows src directory missing", "projects": []}

    candidates: list[str] = []
    for project in sorted(src.rglob("*.csproj")):
        normalized = project.as_posix().lower()
        if normalized.endswith("aevrix.core.csproj") or normalized.endswith("aevrix.enginehost.csproj"):
            continue
        try:
            text = project.read_text(encoding="utf-8")
        except OSError:
            continue
        lowered = text.lower()
        signals = (
            "<outputtype>winexe</outputtype>",
            "<usewinui>true</usewinui>",
            "microsoft.windowsappsdk",
        )
        if any(signal in lowered for signal in signals):
            candidates.append(project.relative_to(repo_root).as_posix())

    if not candidates:
        return {
            "status": BLOCKED,
            "reason": "No physical Desktop/WinUI project was detected; end-to-end product smoke cannot run.",
            "projects": [],
        }
    return {
        "status": PASS,
        "reason": "Physical Desktop/WinUI project detected. End-to-end execution remains a separate mandatory gate.",
        "projects": candidates,
    }


def load_external_evidence(path: Path | None, source_commit: str) -> tuple[dict, list[str]]:
    if path is None:
        return {}, []
    if not path.is_file():
        return {}, [f"external evidence file does not exist: {path}"]
    try:
        payload = load_json(path)
    except (OSError, json.JSONDecodeError) as exc:
        return {}, [f"external evidence cannot be read: {exc}"]

    errors: list[str] = []
    if payload.get("sourceCommit") != source_commit:
        errors.append("external evidence sourceCommit does not match exact candidate")

    gates = payload.get("gates") or {}
    accepted: dict = {}
    for gate_id, evidence in gates.items():
        if gate_id not in EXTERNAL_GATE_IDS:
            errors.append(f"external evidence cannot override automatic/unknown gate {gate_id}")
            continue
        evidence = evidence or {}
        status = evidence.get("status")
        if status not in ALLOWED_STATUSES:
            errors.append(f"external evidence gate {gate_id}: invalid status {status!r}")
            continue
        if status == PASS:
            digest = str(evidence.get("evidenceSha256") or "")
            reference = str(evidence.get("evidenceRef") or "").strip()
            if not HEX64.fullmatch(digest):
                errors.append(f"external PASS gate {gate_id} requires a valid evidenceSha256")
                continue
            if not reference:
                errors.append(f"external PASS gate {gate_id} requires evidenceRef")
                continue
        artifact_digest = evidence.get("artifactSha256")
        if artifact_digest is not None and not HEX64.fullmatch(str(artifact_digest)):
            errors.append(f"external evidence gate {gate_id}: invalid artifactSha256")
            continue
        accepted[gate_id] = evidence
    return accepted, errors


def candidate_command(args: argparse.Namespace) -> int:
    repo_root = Path(args.repo_root).resolve()
    model_path = (repo_root / args.model).resolve()
    out_dir = Path(args.output_dir).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    model = load_json(model_path)
    model_errors = validate_model(model)
    if model_errors:
        raise SystemExit("invalid readiness model: " + "; ".join(model_errors))

    core = parse_trx(Path(args.core_trx))
    security = parse_trx(Path(args.security_trx))
    orchestration = parse_trx(Path(args.orchestration_trx))
    desktop_surface = discover_desktop_surface(repo_root)

    enginehost_build, enginehost_files = artifact_build_evidence(
        Path(args.enginehost_dir), args.build_outcome, "AEVRIX.EngineHost.exe"
    )
    desktop_build, desktop_files = artifact_build_evidence(
        Path(args.desktop_dir), args.desktop_build_outcome, "AEVRIX.Desktop.exe"
    )
    engine_hashes = {entry["sha256"] for entry in enginehost_files}
    soak = parse_soak(Path(args.soak_json), engine_hashes)

    external_path = Path(args.external_evidence) if args.external_evidence else None
    external_gates, external_errors = load_external_evidence(external_path, args.source_commit)

    exact_gates = {
        "enginehost-build": enginehost_build,
        "desktop-release-build": desktop_build,
        "core-tests": core,
        "remote-security-tests": security,
        "orchestrator-judge-tests": orchestration,
        "enginehost-authenticated-soak": soak,
        "desktop-product-surface": desktop_surface,
        "windows-e2e-runtime": {"status": NOT_RUN},
        "installer-lifecycle": {"status": NOT_RUN},
        "distribution-security": {"status": NOT_RUN},
        "execution-authority-db": {"status": NOT_RUN},
        "performance-stability": {"status": NOT_RUN},
        "ux-accessibility": {"status": NOT_RUN},
    }
    for gate_id, evidence in external_gates.items():
        exact_gates[gate_id] = evidence

    statuses = [entry.get("status") for entry in exact_gates.values()]
    pass_count = sum(status == PASS for status in statuses)
    coverage = round((pass_count / len(statuses)) * 100, 2) if statuses else 0.0

    all_pass = bool(statuses) and all(status == PASS for status in statuses) and not external_errors
    release_decision = "HOMOLOGATED" if model.get("readinessPercent") == 100 and all_pass else "NOT_HOMOLOGATED"

    payload = {
        "schemaVersion": 4,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "target": "Windows",
        "sourceCommit": args.source_commit,
        "repository": args.repository,
        "workflowRunId": args.workflow_run_id,
        "runner": {
            "os": args.runner_os,
            "image": args.runner_image,
            "architecture": args.runner_arch,
            "dotnetSdk": args.dotnet_sdk,
        },
        "readinessPercent": model.get("readinessPercent"),
        "exactCandidatePassCoveragePercent": coverage,
        "releaseDecision": release_decision,
        "modelReleaseDecision": model.get("releaseDecision"),
        "modelSha256": sha256_file(model_path),
        "engineHostArtifacts": enginehost_files,
        "desktopArtifacts": desktop_files,
        "exactCandidateGates": exact_gates,
        "externalEvidenceErrors": external_errors,
    }

    canonical = json.dumps(payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    payload["evidenceSha256"] = hashlib.sha256(canonical).hexdigest()
    evidence_path = out_dir / "candidate-evidence.json"
    evidence_path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    lines = [
        "## AEVRIX Windows readiness evidence",
        "",
        f"- Source commit: `{args.source_commit}`",
        f"- Readiness: **{model.get('readinessPercent')}%**",
        f"- Exact-candidate PASS coverage: **{coverage}%**",
        f"- Release decision: **{release_decision}**",
        f"- Evidence SHA-256: `{payload['evidenceSha256']}`",
        "",
        "| Gate | Status |",
        "|---|---|",
    ]
    for gate_id, entry in exact_gates.items():
        lines.append(f"| `{gate_id}` | **{entry.get('status')}** |")
    if external_errors:
        lines += ["", "External evidence errors:"] + [f"- {error}" for error in external_errors]
    lines += ["", "A successful readiness probe means the measurement executed; it does not mean the product is homologated."]
    summary = "\n".join(lines) + "\n"
    (out_dir / "summary.md").write_text(summary, encoding="utf-8")
    print(summary)

    if args.strict and release_decision != "HOMOLOGATED":
        return 3
    return 0


def validate_command(args: argparse.Namespace) -> int:
    model = load_json(Path(args.model))
    errors = validate_model(model)
    if errors:
        for error in errors:
            print(f"FAIL: {error}")
        return 2
    print(f"PASS: readiness model is internally consistent at {model['readinessPercent']}%")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="AEVRIX fail-closed homologation/readiness tooling")
    sub = parser.add_subparsers(dest="command", required=True)

    validate = sub.add_parser("validate-model")
    validate.add_argument("--model", default="docs/qa/readiness-model.json")
    validate.set_defaults(func=validate_command)

    candidate = sub.add_parser("candidate")
    candidate.add_argument("--repo-root", default=".")
    candidate.add_argument("--model", default="docs/qa/readiness-model.json")
    candidate.add_argument("--output-dir", required=True)
    candidate.add_argument("--source-commit", required=True)
    candidate.add_argument("--repository", required=True)
    candidate.add_argument("--workflow-run-id", default="")
    candidate.add_argument("--runner-os", default="Windows")
    candidate.add_argument("--runner-image", default="unknown")
    candidate.add_argument("--runner-arch", default="unknown")
    candidate.add_argument("--dotnet-sdk", default="unknown")
    candidate.add_argument("--build-outcome", choices=("success", "failure", "cancelled", "skipped"), required=True)
    candidate.add_argument("--enginehost-dir", required=True)
    candidate.add_argument("--desktop-build-outcome", choices=("success", "failure", "cancelled", "skipped"), required=True)
    candidate.add_argument("--desktop-dir", required=True)
    candidate.add_argument("--core-trx", required=True)
    candidate.add_argument("--security-trx", required=True)
    candidate.add_argument("--orchestration-trx", required=True)
    candidate.add_argument("--soak-json", required=True)
    candidate.add_argument("--external-evidence")
    candidate.add_argument("--strict", action="store_true")
    candidate.set_defaults(func=candidate_command)

    args = parser.parse_args()
    return int(args.func(args))


if __name__ == "__main__":
    sys.exit(main())
