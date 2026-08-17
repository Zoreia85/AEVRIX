#!/usr/bin/env python3
from __future__ import annotations

import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REGISTRY = ROOT / "docs" / "manifests" / "repository-intelligence.json"
REPO_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
REVISION_RE = re.compile(r"(?:[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})")
SHA256_RE = re.compile(r"[0-9A-Fa-f]{64}")
ALLOWED_MODES = {"Reference", "DiscoverySeed", "Adapter", "OptionalTool", "Vendored", "Blocked"}
EXECUTABLE_MODES = {"Adapter", "OptionalTool", "Vendored"}
RUNTIME_APPROVALS = {"Denied", "PendingPinnedAdapterReview", "PendingSandboxReview", "Approved"}
SECURITY_REVIEWS = {"DeniedExecution", "PendingIndependentReview", "Approved"}
ARTIFACT_CONSUMPTION = {"None", "SourceSnapshot", "ReleaseArtifact", "VendoredSubset"}
PROVENANCE_STATUSES = {"ObservedRevisionOnly", "VerifiedArtifact", "VendoredProvenance"}
DOCUMENT_STATUSES = {"NotApplicable", "RequiredPending", "Verified"}
RESOURCE_BOUNDARIES = ("network", "filesystem", "credentials", "workspace")
APPROVED_RUNTIME = "Approved"

REQUIRED_REPOSITORIES = {
    "ollama/ollama",
    "sindresorhus/awesome",
    "OpenHands/OpenHands",
    "Shubhamsaboo/awesome-llm-apps",
    "langflow-ai/langflow",
    "punkpeye/awesome-mcp-servers",
    "nexu-io/open-design",
    "public-apis/public-apis",
    "D4Vinci/Scrapling",
    "microsoft/mxc",
    "ripienaar/free-for-dev",
}
DISCOVERY_ONLY = {
    "sindresorhus/awesome",
    "punkpeye/awesome-mcp-servers",
    "public-apis/public-apis",
}
REFERENCE_ONLY_DENIED = {"microsoft/mxc", "ripienaar/free-for-dev"}


def _is_utc_timestamp(value: object) -> bool:
    if not isinstance(value, str) or not value.endswith("Z"):
        return False
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        return False
    return parsed.tzinfo is not None and parsed.utcoffset() == timezone.utc.utcoffset(parsed)


def _nonempty_string_list(value: object) -> bool:
    return isinstance(value, list) and bool(value) and all(isinstance(item, str) and item.strip() for item in value)


def _full_revision(value: object) -> bool:
    return isinstance(value, str) and REVISION_RE.fullmatch(value) is not None


def audit_registry(data: dict) -> list[str]:
    failures: list[str] = []
    if data.get("schemaVersion") != 2:
        failures.append("schemaVersion must be 2")
    if not _is_utc_timestamp(data.get("verifiedAtUtc")):
        failures.append("verifiedAtUtc must be an ISO-8601 UTC timestamp ending in Z")

    policy = data.get("policy") or {}
    if policy.get("defaultRuntimeApproval") != "Denied":
        failures.append("default runtime approval must be Denied")
    for flag in (
        "requireObservedRevision",
        "requirePinnedRevisionForExecution",
        "requireArtifactSha256ForConsumption",
        "requireLicenseVerificationForVendoring",
        "requireIndependentSecurityReviewForExecution",
        "requireExplicitResourceBoundaries",
        "requireProvenanceForConsumption",
        "discoveryNeverImpliesExecutionApproval",
    ):
        if policy.get(flag) is not True:
            failures.append(f"policy.{flag} must be true")

    repos = data.get("repositories")
    if not isinstance(repos, list):
        return failures + ["repositories must be a list"]

    seen: set[str] = set()
    for index, record in enumerate(repos):
        prefix = f"repositories[{index}]"
        if not isinstance(record, dict):
            failures.append(f"{prefix} must be an object")
            continue
        name = record.get("repository")
        if not isinstance(name, str) or not REPO_RE.fullmatch(name):
            failures.append(f"{prefix}.repository is invalid")
            continue
        if name in seen:
            failures.append(f"duplicate repository: {name}")
        seen.add(name)

        if record.get("archived") is not False:
            failures.append(f"{name}: archived or unverified repositories must not be active seeds")
        if not isinstance(record.get("defaultBranch"), str) or not record.get("defaultBranch", "").strip():
            failures.append(f"{name}: defaultBranch is required")
        license_spdx = record.get("licenseSpdx")
        if not isinstance(license_spdx, str) or not license_spdx.strip():
            failures.append(f"{name}: licenseSpdx is required")

        observed_revision = record.get("observedRevision")
        if not _full_revision(observed_revision):
            failures.append(f"{name}: observedRevision must be a full 40- or 64-character hexadecimal revision")
        if not _is_utc_timestamp(record.get("verifiedAtUtc")):
            failures.append(f"{name}: verifiedAtUtc is required and must be UTC")

        modes = record.get("integrationModes")
        if not isinstance(modes, list) or not modes or any(mode not in ALLOWED_MODES for mode in modes):
            failures.append(f"{name}: invalid integrationModes")
            modes = []
        approval = record.get("runtimeApproval")
        if approval not in RUNTIME_APPROVALS:
            failures.append(f"{name}: invalid runtimeApproval")
        review = record.get("securityReview")
        if review not in SECURITY_REVIEWS:
            failures.append(f"{name}: invalid securityReview")

        if not _nonempty_string_list(record.get("securityConstraints")):
            failures.append(f"{name}: non-empty securityConstraints are required")
        if not _nonempty_string_list(record.get("allowedCapabilities")):
            failures.append(f"{name}: non-empty allowedCapabilities are required")
        if not _nonempty_string_list(record.get("deniedCapabilities")):
            failures.append(f"{name}: non-empty deniedCapabilities are required")

        boundaries = record.get("resourceBoundaries")
        if not isinstance(boundaries, dict):
            failures.append(f"{name}: resourceBoundaries object is required")
        else:
            for boundary in RESOURCE_BOUNDARIES:
                value = boundaries.get(boundary)
                if not isinstance(value, str) or not value.strip():
                    failures.append(f"{name}: resourceBoundaries.{boundary} is required")

        pinned_revision = record.get("pinnedRevision")
        if pinned_revision is not None and not _full_revision(pinned_revision):
            failures.append(f"{name}: pinnedRevision must be null or a full 40- or 64-character hexadecimal revision")

        consumption = record.get("artifactConsumption")
        if consumption not in ARTIFACT_CONSUMPTION:
            failures.append(f"{name}: invalid artifactConsumption")
        consumed_sha = record.get("consumedArtifactSha256")
        provenance = record.get("provenanceStatus")
        sbom = record.get("sbomStatus")
        notice = record.get("noticeStatus")
        if provenance not in PROVENANCE_STATUSES:
            failures.append(f"{name}: invalid provenanceStatus")
        if sbom not in DOCUMENT_STATUSES:
            failures.append(f"{name}: invalid sbomStatus")
        if notice not in DOCUMENT_STATUSES:
            failures.append(f"{name}: invalid noticeStatus")

        if consumption == "None":
            if consumed_sha is not None:
                failures.append(f"{name}: consumedArtifactSha256 must be null when no artifact is consumed")
            if provenance != "ObservedRevisionOnly":
                failures.append(f"{name}: no-consumption records must use ObservedRevisionOnly provenance")
            if sbom != "NotApplicable" or notice != "NotApplicable":
                failures.append(f"{name}: SBOM/NOTICE must be NotApplicable when no artifact is consumed")
        elif consumption in ARTIFACT_CONSUMPTION:
            if not isinstance(consumed_sha, str) or SHA256_RE.fullmatch(consumed_sha) is None:
                failures.append(f"{name}: consumed artifacts require an exact SHA-256")
            if provenance == "ObservedRevisionOnly":
                failures.append(f"{name}: consumed artifacts require artifact-level provenance")
            if sbom == "NotApplicable" or notice == "NotApplicable":
                failures.append(f"{name}: consumed artifacts require explicit SBOM/NOTICE disposition")

        if name in DISCOVERY_ONLY:
            if modes != ["DiscoverySeed"]:
                failures.append(f"{name}: discovery catalogs must remain DiscoverySeed-only")
            if approval != "Denied" or review != "DeniedExecution":
                failures.append(f"{name}: discovery catalogs can never be runtime-approved")
            if consumption != "None":
                failures.append(f"{name}: discovery catalogs cannot be consumed as executable artifacts")

        if name in REFERENCE_ONLY_DENIED:
            if modes != ["Reference"]:
                failures.append(f"{name}: governed reference-only repositories must remain Reference-only")
            if approval != "Denied" or review != "DeniedExecution":
                failures.append(f"{name}: governed reference-only repositories must remain runtime Denied")

        required_constraints: set[str] = set()
        required_denials: set[str] = set()
        if name == "ollama/ollama":
            required_constraints = {"loopback-default", "no-implicit-model-pull", "model-allowlist", "judge-before-trust"}
            required_denials = {"brain-authority", "implicit-model-download", "host-secret-access"}
        elif name == "OpenHands/OpenHands":
            required_constraints = {"sandbox-required", "workspace-scope", "no-host-secrets", "judge-before-trust"}
            required_denials = {"unrestricted-host-filesystem", "host-secret-access", "host-process-authority"}
        elif name == "D4Vinci/Scrapling":
            required_constraints = {"authorization-required", "no-captcha-bypass", "no-cloudflare-bypass", "no-anti-bot-evasion", "respect-access-controls"}
            required_denials = {"anti-bot-bypass", "captcha-bypass", "cloudflare-bypass", "access-control-evasion", "turnstile-bypass", "stealth-browser-mode"}
        missing_constraints = sorted(required_constraints - set(record.get("securityConstraints") or []))
        missing_denials = sorted(required_denials - set(record.get("deniedCapabilities") or []))
        if missing_constraints:
            failures.append(f"{name}: missing mandatory security constraints: " + ", ".join(missing_constraints))
        if missing_denials:
            failures.append(f"{name}: missing mandatory denied capabilities: " + ", ".join(missing_denials))

        if name == "ripienaar/free-for-dev" and license_spdx != "NOASSERTION":
            failures.append("ripienaar/free-for-dev: licensing must remain NOASSERTION until independently proven")
        if license_spdx == "NOASSERTION" and ("Vendored" in modes or approval == APPROVED_RUNTIME):
            failures.append(f"{name}: unverified licensing forbids vendoring/runtime approval")

        if approval == APPROVED_RUNTIME:
            if not EXECUTABLE_MODES.intersection(modes):
                failures.append(f"{name}: Approved runtime requires an executable integration mode")
            if review != "Approved":
                failures.append(f"{name}: Approved runtime requires Approved independent security review")
            if not _full_revision(pinned_revision):
                failures.append(f"{name}: Approved runtime requires pinnedRevision")
            if consumption == "None" or not isinstance(consumed_sha, str) or SHA256_RE.fullmatch(consumed_sha) is None:
                failures.append(f"{name}: Approved runtime requires a SHA-256-bound consumed artifact")
            if license_spdx == "NOASSERTION":
                failures.append(f"{name}: Approved runtime requires verified licensing")

    missing = sorted(REQUIRED_REPOSITORIES - seen)
    if missing:
        failures.append("missing required repository seeds: " + ", ".join(missing))
    return failures


def main() -> int:
    if not REGISTRY.is_file():
        print("repository intelligence registry missing", file=sys.stderr)
        return 1
    try:
        data = json.loads(REGISTRY.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(f"registry cannot be read: {exc}", file=sys.stderr)
        return 1
    failures = audit_registry(data)
    print(json.dumps({
        "status": "PASS" if not failures else "FAIL",
        "repositories": len(data.get("repositories") or []),
        "failures": failures,
    }, indent=2, ensure_ascii=False))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
