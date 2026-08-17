from __future__ import annotations

from datetime import datetime
import re

HEX40 = re.compile(r"^[0-9a-f]{40}$")
CURRENT_TERMS_REVISION = "preview-authorized-use-v1"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def _parse_timestamp(value: object, field_name: str) -> str:
    text = str(value or "").strip()
    require(bool(text), f"{field_name} is missing")
    normalized = text.replace("Z", "+00:00")
    try:
        parsed = datetime.fromisoformat(normalized)
    except ValueError as exc:
        raise ValueError(f"{field_name} is not a valid ISO-8601 timestamp") from exc
    require(parsed.tzinfo is not None, f"{field_name} must include timezone information")
    return text


def validate_first_run_evidence(first_run: dict, expected_commit: str, expected_version: str) -> dict:
    expected = expected_commit.lower()
    require(bool(HEX40.fullmatch(expected)), "expected source commit must be an exact lowercase 40-character SHA")
    require(first_run.get("schemaVersion") == 2, "first-run evidence schemaVersion must be 2")
    require(str(first_run.get("candidateSha") or "").lower() == expected, "first-run candidateSha does not match exact candidate")
    require(first_run.get("productVersion") == expected_version, "first-run productVersion does not match installer candidate")
    require(first_run.get("termsRevision") == CURRENT_TERMS_REVISION, "first-run terms revision is not current")
    require(first_run.get("installExitCode") == 0, "first-run AVA install did not exit 0")
    require(first_run.get("uninstallExitCode") == 0, "first-run AVA uninstall did not exit 0")

    required_true = (
        "presentationObserved",
        "preAcceptanceNavigationAbsent",
        "declineExitedWithoutAcceptance",
        "initialAcceptDisabled",
        "explicitConfirmationRequired",
        "acceptancePersisted",
        "commandCenterTransitionObserved",
        "secondLaunchSkippedFirstRun",
        "acceptanceSurvivedUninstall",
    )
    for field in required_true:
        require(first_run.get(field) is True, f"first-run evidence requires {field}=true")

    presented_at = _parse_timestamp(first_run.get("presentedAtUtc"), "presentedAtUtc")
    accepted_at = _parse_timestamp(first_run.get("acceptedAtUtc"), "acceptedAtUtc")

    return {
        "candidateSha": expected,
        "productVersion": expected_version,
        "termsRevision": CURRENT_TERMS_REVISION,
        "presentedAtUtc": presented_at,
        "acceptedAtUtc": accepted_at,
        **{field: True for field in required_true},
        "installExitCode": 0,
        "uninstallExitCode": 0,
    }
