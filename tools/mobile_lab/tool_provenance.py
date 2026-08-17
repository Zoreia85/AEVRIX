from __future__ import annotations

import re
import urllib.parse
from dataclasses import dataclass
from typing import Any, Mapping

from .integration_http import GitHubPublicApiClient, JsonIntegrationResult

_REPO_PART = re.compile(r"^[A-Za-z0-9_.-]{1,100}$")
_QUERY_PART = re.compile(r"^[A-Za-z0-9_.:@+\-/]{1,255}$")


def _safe(value: str, regex: re.Pattern[str], label: str) -> str:
    if not regex.fullmatch(value):
        raise ValueError(f"invalid {label}")
    return value


class GitHubProvenanceApiClient(GitHubPublicApiClient):
    """Read-only GitHub provenance endpoints layered on the public intelligence client."""

    def latest_release(self, owner: str, repo: str,
                       etag: str | None = None) -> JsonIntegrationResult:
        owner = _safe(owner, _REPO_PART, "GitHub owner")
        repo = _safe(repo, _REPO_PART, "GitHub repo")
        return self._get(f"/repos/{owner}/{repo}/releases/latest", "latest-release", etag)

    def release_asset(self, owner: str, repo: str, asset_id: int,
                      etag: str | None = None) -> JsonIntegrationResult:
        if asset_id <= 0:
            raise ValueError("asset_id must be positive")
        owner = _safe(owner, _REPO_PART, "GitHub owner")
        repo = _safe(repo, _REPO_PART, "GitHub repo")
        return self._get(f"/repos/{owner}/{repo}/releases/assets/{asset_id}", "release-asset", etag)

    def commit(self, owner: str, repo: str, ref: str,
               etag: str | None = None) -> JsonIntegrationResult:
        owner = _safe(owner, _REPO_PART, "GitHub owner")
        repo = _safe(repo, _REPO_PART, "GitHub repo")
        ref = _safe(ref, _QUERY_PART, "commit ref")
        encoded_ref = urllib.parse.quote(ref, safe="")
        return self._get(f"/repos/{owner}/{repo}/commits/{encoded_ref}", "commit", etag)

    @staticmethod
    def _subject_digest(sha256_hex: str) -> str:
        value = sha256_hex.strip().lower()
        if not re.fullmatch(r"[0-9a-f]{64}", value):
            raise ValueError("subject digest must be a SHA-256 hex digest")
        return "sha256:" + value

    def user_attestations(self, username: str, sha256_hex: str,
                          predicate_type: str | None = None,
                          etag: str | None = None) -> JsonIntegrationResult:
        username = _safe(username, _REPO_PART, "GitHub username")
        digest = urllib.parse.quote(self._subject_digest(sha256_hex), safe=":")
        query = ""
        if predicate_type:
            query = "?" + urllib.parse.urlencode({
                "predicate_type": _safe(predicate_type, _QUERY_PART, "predicate type")
            })
        return self._get(f"/users/{username}/attestations/{digest}{query}", "user-attestations", etag)

    def organization_attestations(self, organization: str, sha256_hex: str,
                                  predicate_type: str | None = None,
                                  etag: str | None = None) -> JsonIntegrationResult:
        organization = _safe(organization, _REPO_PART, "GitHub organization")
        digest = urllib.parse.quote(self._subject_digest(sha256_hex), safe=":")
        query = ""
        if predicate_type:
            query = "?" + urllib.parse.urlencode({
                "predicate_type": _safe(predicate_type, _QUERY_PART, "predicate type")
            })
        return self._get(
            f"/orgs/{organization}/attestations/{digest}{query}",
            "organization-attestations", etag
        )


@dataclass(frozen=True)
class ReleaseAssetProvenance:
    asset_id: int
    name: str
    size: int | None
    digest: str | None
    digest_status: str


@dataclass(frozen=True)
class ToolProvenanceSnapshot:
    repository: str
    release_tag: str | None
    release_published_at: str | None
    release_draft: bool | None
    release_prerelease: bool | None
    assets: tuple[ReleaseAssetProvenance, ...]
    commit_sha: str | None
    commit_signature_verified: bool | None
    commit_verification_reason: str | None
    attestation_count: int | None
    attestation_status: str


def summarize_release(repository: str, release: Mapping[str, Any],
                      commit: Mapping[str, Any] | None = None,
                      attestations: Mapping[str, Any] | None = None) -> ToolProvenanceSnapshot:
    assets: list[ReleaseAssetProvenance] = []
    for item in release.get("assets") or []:
        digest = item.get("digest")
        status = "SHA256_PRESENT" if isinstance(digest, str) and digest.startswith("sha256:") else "UNMEASURED"
        assets.append(ReleaseAssetProvenance(
            asset_id=int(item.get("id", 0)),
            name=str(item.get("name", "")),
            size=int(item["size"]) if item.get("size") is not None else None,
            digest=str(digest) if digest is not None else None,
            digest_status=status,
        ))

    sha = verified = reason = None
    if commit is not None:
        sha = str(commit.get("sha", "")) or None
        verification = (commit.get("commit") or {}).get("verification") or commit.get("verification") or {}
        verified_value = verification.get("verified")
        verified = bool(verified_value) if isinstance(verified_value, bool) else None
        reason = str(verification.get("reason")) if verification.get("reason") is not None else None

    count: int | None = None
    attestation_status = "UNMEASURED"
    if attestations is not None:
        rows = attestations.get("attestations")
        if isinstance(rows, list):
            count = len(rows)
            # Presence is evidence of association only; cryptographic verification is separate.
            attestation_status = "PRESENT_UNVERIFIED" if count else "NONE_FOUND"

    return ToolProvenanceSnapshot(
        repository=repository,
        release_tag=str(release.get("tag_name")) if release.get("tag_name") is not None else None,
        release_published_at=str(release.get("published_at")) if release.get("published_at") is not None else None,
        release_draft=release.get("draft") if isinstance(release.get("draft"), bool) else None,
        release_prerelease=release.get("prerelease") if isinstance(release.get("prerelease"), bool) else None,
        assets=tuple(assets),
        commit_sha=sha,
        commit_signature_verified=verified,
        commit_verification_reason=reason,
        attestation_count=count,
        attestation_status=attestation_status,
    )
