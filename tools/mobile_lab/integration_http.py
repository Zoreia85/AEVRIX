from __future__ import annotations

import hashlib
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Mapping, Protocol, Sequence

MAX_RESPONSE_BYTES = 4 * 1024 * 1024
GITHUB_API_VERSION = "2026-03-10"
USER_AGENT = "AEVRIX-Mobile-Lab/0.3"


class IntegrationPolicyError(RuntimeError):
    pass


@dataclass(frozen=True)
class HttpRequest:
    method: str
    url: str
    headers: Mapping[str, str] = field(default_factory=dict)
    body: bytes | None = None
    timeout_seconds: float = 20.0
    max_response_bytes: int = MAX_RESPONSE_BYTES


@dataclass(frozen=True)
class HttpResponse:
    status: int
    url: str
    headers: Mapping[str, str]
    body: bytes


class HttpTransport(Protocol):
    def send(self, request: HttpRequest) -> HttpResponse: ...


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise IntegrationPolicyError(f"redirect blocked: {req.full_url} -> {newurl}")


class BoundedHttpsTransport:
    """HTTPS-only transport with host allowlist, no redirects and bounded responses."""

    def __init__(self, allowed_hosts: Sequence[str]) -> None:
        hosts = {h.strip().lower().rstrip(".") for h in allowed_hosts if h.strip()}
        if not hosts:
            raise ValueError("at least one allowed host is required")
        self._allowed_hosts = frozenset(hosts)
        self._opener = urllib.request.build_opener(_NoRedirect())

    def _validate_url(self, url: str) -> None:
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme.lower() != "https":
            raise IntegrationPolicyError("external integrations require HTTPS")
        if parsed.username or parsed.password:
            raise IntegrationPolicyError("credentials are not permitted in integration URLs")
        host = (parsed.hostname or "").lower().rstrip(".")
        if host not in self._allowed_hosts:
            raise IntegrationPolicyError(f"host is not allowlisted: {host}")
        if parsed.port not in (None, 443):
            raise IntegrationPolicyError("external integrations may only use port 443")

    def send(self, request: HttpRequest) -> HttpResponse:
        self._validate_url(request.url)
        method = request.method.upper().strip()
        if method not in {"GET", "HEAD", "POST"}:
            raise IntegrationPolicyError(f"HTTP method is not permitted: {method}")
        if not 0.1 <= request.timeout_seconds <= 60.0:
            raise IntegrationPolicyError("timeout must be 0.1-60 seconds")
        if not 1 <= request.max_response_bytes <= 16 * 1024 * 1024:
            raise IntegrationPolicyError("response bound must be 1 byte-16 MiB")
        req = urllib.request.Request(
            request.url, data=request.body, headers=dict(request.headers), method=method
        )
        try:
            with self._opener.open(req, timeout=request.timeout_seconds) as response:
                final_url = response.geturl()
                self._validate_url(final_url)
                body = response.read(request.max_response_bytes + 1)
                if len(body) > request.max_response_bytes:
                    raise IntegrationPolicyError("response exceeded configured byte limit")
                return HttpResponse(
                    int(response.status), final_url,
                    {k: v for k, v in response.headers.items()}, body
                )
        except IntegrationPolicyError:
            raise
        except urllib.error.HTTPError as exc:
            body = exc.read(request.max_response_bytes + 1)
            if len(body) > request.max_response_bytes:
                raise IntegrationPolicyError("error response exceeded configured byte limit") from exc
            return HttpResponse(
                int(exc.code), exc.geturl(),
                {k: v for k, v in exc.headers.items()}, body
            )


@dataclass(frozen=True)
class RateLimitSnapshot:
    limit: int | None
    remaining: int | None
    reset_epoch: int | None
    resource: str | None

    @classmethod
    def from_github_headers(cls, headers: Mapping[str, str]) -> "RateLimitSnapshot":
        lower = {k.lower(): v for k, v in headers.items()}
        def integer(name: str) -> int | None:
            try:
                return int(lower[name]) if name in lower else None
            except ValueError:
                return None
        return cls(
            integer("x-ratelimit-limit"),
            integer("x-ratelimit-remaining"),
            integer("x-ratelimit-reset"),
            lower.get("x-ratelimit-resource"),
        )


@dataclass(frozen=True)
class IntegrationEvidence:
    source_id: str
    operation: str
    requested_url: str
    response_url: str
    status: int
    observed_at: str
    request_sha256: str
    response_sha256: str
    response_bytes: int
    rate_limit: RateLimitSnapshot | None = None
    etag: str | None = None


@dataclass(frozen=True)
class JsonIntegrationResult:
    data: Any
    evidence: IntegrationEvidence
    not_modified: bool = False


def _header(headers: Mapping[str, str], name: str) -> str | None:
    lower = name.lower()
    return next((v for k, v in headers.items() if k.lower() == lower), None)


def _request_hash(method: str, url: str, body: bytes | None) -> str:
    # Credentials/headers are deliberately excluded from the durable request proof.
    material = method.upper().encode() + b"\0" + url.encode() + b"\0" + (body or b"")
    return hashlib.sha256(material).hexdigest()


def _json_result(source: str, operation: str, req: HttpRequest, res: HttpResponse,
                 rate: RateLimitSnapshot | None = None) -> JsonIntegrationResult:
    if res.status == 304:
        data, not_modified = None, True
    else:
        try:
            data = json.loads(res.body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise IntegrationPolicyError(f"{source} returned non-JSON content") from exc
        not_modified = False
    return JsonIntegrationResult(
        data=data,
        not_modified=not_modified,
        evidence=IntegrationEvidence(
            source_id=source,
            operation=operation,
            requested_url=req.url,
            response_url=res.url,
            status=res.status,
            observed_at=datetime.now(timezone.utc).isoformat(),
            request_sha256=_request_hash(req.method, req.url, req.body),
            response_sha256=hashlib.sha256(res.body).hexdigest(),
            response_bytes=len(res.body),
            rate_limit=rate,
            etag=_header(res.headers, "etag"),
        ),
    )


_REPO_PART = re.compile(r"^[A-Za-z0-9_.-]{1,100}$")
_QUERY_PART = re.compile(r"^[A-Za-z0-9_.:@+\-/]{1,255}$")


def _safe(value: str, regex: re.Pattern[str], label: str) -> str:
    if not regex.fullmatch(value):
        raise ValueError(f"invalid {label}")
    return value


class GitHubPublicApiClient:
    """Read-only GitHub intelligence. No mutations or secret-management surface."""

    API_ROOT = "https://api.github.com"

    def __init__(self, transport: HttpTransport | None = None, token: str | None = None,
                 api_version: str = GITHUB_API_VERSION) -> None:
        self._transport = transport or BoundedHttpsTransport(["api.github.com"])
        self._token = token or os.getenv("GITHUB_TOKEN")
        self._api_version = api_version

    def _headers(self, etag: str | None = None) -> dict[str, str]:
        headers = {
            "Accept": "application/vnd.github+json",
            "User-Agent": USER_AGENT,
            "X-GitHub-Api-Version": self._api_version,
        }
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        if etag:
            headers["If-None-Match"] = etag
        return headers

    def _get(self, path: str, operation: str, etag: str | None = None) -> JsonIntegrationResult:
        if not path.startswith("/") or "://" in path:
            raise IntegrationPolicyError("GitHub path must be relative to pinned API root")
        req = HttpRequest("GET", self.API_ROOT + path, self._headers(etag))
        res = self._transport.send(req)
        return _json_result(
            "github-rest-public", operation, req, res,
            RateLimitSnapshot.from_github_headers(res.headers)
        )

    def repository(self, owner: str, repo: str, etag: str | None = None) -> JsonIntegrationResult:
        owner = _safe(owner, _REPO_PART, "GitHub owner")
        repo = _safe(repo, _REPO_PART, "GitHub repo")
        return self._get(f"/repos/{owner}/{repo}", "repository", etag)

    def releases(self, owner: str, repo: str, per_page: int = 30,
                 etag: str | None = None) -> JsonIntegrationResult:
        if not 1 <= per_page <= 100:
            raise ValueError("per_page must be 1-100")
        owner = _safe(owner, _REPO_PART, "GitHub owner")
        repo = _safe(repo, _REPO_PART, "GitHub repo")
        return self._get(f"/repos/{owner}/{repo}/releases?per_page={per_page}", "releases", etag)

    def workflow_artifacts(self, owner: str, repo: str, run_id: int,
                           per_page: int = 100, etag: str | None = None) -> JsonIntegrationResult:
        if run_id <= 0:
            raise ValueError("run_id must be positive")
        if not 1 <= per_page <= 100:
            raise ValueError("per_page must be 1-100")
        owner = _safe(owner, _REPO_PART, "GitHub owner")
        repo = _safe(repo, _REPO_PART, "GitHub repo")
        path = f"/repos/{owner}/{repo}/actions/runs/{run_id}/artifacts?per_page={per_page}"
        return self._get(path, "workflow-artifacts", etag)

    def dependency_sbom(self, owner: str, repo: str, etag: str | None = None) -> JsonIntegrationResult:
        owner = _safe(owner, _REPO_PART, "GitHub owner")
        repo = _safe(repo, _REPO_PART, "GitHub repo")
        return self._get(f"/repos/{owner}/{repo}/dependency-graph/sbom", "dependency-sbom", etag)

    def global_advisories(self, ecosystem: str | None = None, package: str | None = None,
                          per_page: int = 30, etag: str | None = None) -> JsonIntegrationResult:
        if not 1 <= per_page <= 100:
            raise ValueError("per_page must be 1-100")
        query = [("per_page", str(per_page))]
        if ecosystem:
            query.append(("ecosystem", _safe(ecosystem, _QUERY_PART, "ecosystem")))
        if package:
            query.append(("affects", _safe(package, _QUERY_PART, "package")))
        return self._get("/advisories?" + urllib.parse.urlencode(query), "global-advisories", etag)


class OSVApiClient:
    API_ROOT = "https://api.osv.dev"

    def __init__(self, transport: HttpTransport | None = None) -> None:
        self._transport = transport or BoundedHttpsTransport(["api.osv.dev"])

    def _post(self, path: str, payload: Mapping[str, Any], operation: str) -> JsonIntegrationResult:
        body = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()
        req = HttpRequest("POST", self.API_ROOT + path, {
            "Accept": "application/json", "Content-Type": "application/json", "User-Agent": USER_AGENT
        }, body)
        res = self._transport.send(req)
        return _json_result("osv-api", operation, req, res)

    def query_package(self, name: str, ecosystem: str,
                      version: str | None = None) -> JsonIntegrationResult:
        if not name.strip() or not ecosystem.strip():
            raise ValueError("name and ecosystem are required")
        payload: dict[str, Any] = {"package": {"name": name.strip(), "ecosystem": ecosystem.strip()}}
        if version:
            payload["version"] = version.strip()
        return self._post("/v1/query", payload, "query-package")

    def query_commit(self, commit_sha: str) -> JsonIntegrationResult:
        value = commit_sha.strip().lower()
        if not re.fullmatch(r"[0-9a-f]{7,64}", value):
            raise ValueError("commit must be a hexadecimal source revision")
        return self._post("/v1/query", {"commit": value}, "query-commit")

    def query_batch(self, queries: Sequence[Mapping[str, Any]]) -> JsonIntegrationResult:
        if not 1 <= len(queries) <= 1000:
            raise ValueError("batch must contain 1-1000 queries")
        return self._post("/v1/querybatch", {"queries": list(queries)}, "query-batch")
