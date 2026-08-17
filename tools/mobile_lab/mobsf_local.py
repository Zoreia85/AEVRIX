from __future__ import annotations

import hashlib
import http.client
import json
import os
import secrets
import urllib.parse
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping, Protocol

from .integration_http import IntegrationPolicyError
from .integration_tools import MobSFLocalEndpoint
from .tool_invocation import AuthorizedArtifact

_ALLOWED_PATHS = frozenset({
    "/api/v1/upload",
    "/api/v1/scan",
    "/api/v1/report_json",
    "/api/v1/delete_scan",
})
_SCAN_HASH = __import__("re").compile(r"^[0-9a-fA-F]{16,128}$")


@dataclass(frozen=True)
class MobSFResponse:
    status: int
    headers: Mapping[str, str]
    body: bytes


class MobSFTransport(Protocol):
    def post_form(self, path: str, fields: Mapping[str, str], api_key: str) -> MobSFResponse: ...
    def post_file(self, path: str, field_name: str, file_path: str, api_key: str) -> MobSFResponse: ...


class LoopbackMobSFTransport:
    """Streaming loopback-only MobSF transport with no redirect following."""

    def __init__(self, endpoint: MobSFLocalEndpoint, timeout_seconds: float = 120.0,
                 max_response_bytes: int = 32 * 1024 * 1024,
                 max_upload_bytes: int = 1024 * 1024 * 1024) -> None:
        self._endpoint = endpoint.validate()
        if not 0.1 <= timeout_seconds <= 600.0:
            raise ValueError("MobSF timeout must be 0.1-600 seconds")
        self._timeout = timeout_seconds
        self._max_response = max_response_bytes
        self._max_upload = max_upload_bytes
        self._parsed = urllib.parse.urlsplit(self._endpoint.base_url)

    def _connection(self):
        host = self._parsed.hostname or ""
        port = self._parsed.port or (443 if self._parsed.scheme == "https" else 80)
        cls = http.client.HTTPSConnection if self._parsed.scheme == "https" else http.client.HTTPConnection
        return cls(host, port, timeout=self._timeout)

    @staticmethod
    def _path(path: str) -> str:
        if path not in _ALLOWED_PATHS:
            raise IntegrationPolicyError("MobSF API path is not allowlisted")
        return path

    def _read(self, response: http.client.HTTPResponse) -> MobSFResponse:
        body = response.read(self._max_response + 1)
        if len(body) > self._max_response:
            raise IntegrationPolicyError("MobSF response exceeded configured byte limit")
        return MobSFResponse(response.status, {k: v for k, v in response.getheaders()}, body)

    def post_form(self, path: str, fields: Mapping[str, str], api_key: str) -> MobSFResponse:
        payload = urllib.parse.urlencode(dict(fields)).encode()
        conn = self._connection()
        try:
            conn.request("POST", self._path(path), body=payload, headers={
                "Authorization": api_key,
                "Content-Type": "application/x-www-form-urlencoded",
                "Content-Length": str(len(payload)),
            })
            return self._read(conn.getresponse())
        finally:
            conn.close()

    def post_file(self, path: str, field_name: str, file_path: str, api_key: str) -> MobSFResponse:
        size = os.path.getsize(file_path)
        if size > self._max_upload:
            raise IntegrationPolicyError("artifact exceeds local MobSF upload limit")
        boundary = "aevrix-" + secrets.token_hex(16)
        name = Path(file_path).name.replace('"', "")
        preamble = (
            f"--{boundary}\r\n"
            f"Content-Disposition: form-data; name=\"{field_name}\"; filename=\"{name}\"\r\n"
            "Content-Type: application/octet-stream\r\n\r\n"
        ).encode()
        closing = f"\r\n--{boundary}--\r\n".encode()
        conn = self._connection()
        try:
            conn.putrequest("POST", self._path(path))
            conn.putheader("Authorization", api_key)
            conn.putheader("Content-Type", f"multipart/form-data; boundary={boundary}")
            conn.putheader("Content-Length", str(len(preamble) + size + len(closing)))
            conn.endheaders()
            conn.send(preamble)
            with open(file_path, "rb") as handle:
                for block in iter(lambda: handle.read(1024 * 1024), b""):
                    conn.send(block)
            conn.send(closing)
            return self._read(conn.getresponse())
        finally:
            conn.close()


@dataclass(frozen=True)
class MobSFEvidence:
    operation: str
    status: int
    request_sha256: str
    response_sha256: str
    response_bytes: int
    observed_at: str


@dataclass(frozen=True)
class MobSFScanHandle:
    scan_hash: str
    scan_type: str
    file_name: str
    input_sha256: str


@dataclass(frozen=True)
class MobSFAnalysisResult:
    handle: MobSFScanHandle
    report: Mapping[str, Any]
    evidence: tuple[MobSFEvidence, ...]
    cleanup_completed: bool


class MobSFLocalApiClient:
    """Artifact analysis is permitted only against a confirmed isolated loopback MobSF instance."""

    def __init__(self, endpoint: MobSFLocalEndpoint, api_key: str,
                 network_isolation_confirmed: bool,
                 transport: MobSFTransport | None = None) -> None:
        endpoint.validate()
        if not api_key.strip():
            raise ValueError("MobSF API key is required")
        if not network_isolation_confirmed:
            raise IntegrationPolicyError("MobSF server network isolation must be explicitly confirmed")
        self._api_key = api_key
        self._transport = transport or LoopbackMobSFTransport(endpoint)

    @staticmethod
    def _artifact_hash(path: str) -> str:
        digest = hashlib.sha256()
        with open(path, "rb") as handle:
            for block in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(block)
        return digest.hexdigest()

    @staticmethod
    def _decode(operation: str, request_material: bytes, response: MobSFResponse) -> tuple[Any, MobSFEvidence]:
        try:
            data = json.loads(response.body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise IntegrationPolicyError(f"MobSF {operation} returned non-JSON content") from exc
        evidence = MobSFEvidence(
            operation=operation,
            status=response.status,
            request_sha256=hashlib.sha256(request_material).hexdigest(),
            response_sha256=hashlib.sha256(response.body).hexdigest(),
            response_bytes=len(response.body),
            observed_at=datetime.now(timezone.utc).isoformat(),
        )
        if response.status < 200 or response.status >= 300:
            raise IntegrationPolicyError(f"MobSF {operation} failed with HTTP {response.status}")
        return data, evidence

    def upload(self, artifact: AuthorizedArtifact) -> tuple[MobSFScanHandle, MobSFEvidence]:
        artifact.validate()
        if artifact.kind not in {"apk", "ipa"}:
            raise IntegrationPolicyError("MobSF local v0.5 upload supports APK or IPA")
        observed_hash = self._artifact_hash(artifact.path)
        if observed_hash != artifact.sha256.lower():
            raise IntegrationPolicyError("artifact bytes do not match authorized SHA-256")
        response = self._transport.post_file("/api/v1/upload", "file", artifact.path, self._api_key)
        data, evidence = self._decode("upload", b"upload\0" + artifact.sha256.lower().encode(), response)
        scan_hash = str(data.get("hash", ""))
        scan_type = str(data.get("scan_type", ""))
        file_name = str(data.get("file_name") or data.get("file") or Path(artifact.path).name)
        if not _SCAN_HASH.fullmatch(scan_hash) or not scan_type.strip():
            raise IntegrationPolicyError("MobSF upload response is missing a valid hash/scan_type")
        return MobSFScanHandle(scan_hash, scan_type, file_name, artifact.sha256.lower()), evidence

    def scan(self, handle: MobSFScanHandle) -> MobSFEvidence:
        fields = {"hash": handle.scan_hash, "scan_type": handle.scan_type, "file_name": handle.file_name}
        response = self._transport.post_form("/api/v1/scan", fields, self._api_key)
        _, evidence = self._decode("scan", json.dumps(fields, sort_keys=True).encode(), response)
        return evidence

    def report_json(self, handle: MobSFScanHandle) -> tuple[Mapping[str, Any], MobSFEvidence]:
        fields = {"hash": handle.scan_hash, "scan_type": handle.scan_type}
        response = self._transport.post_form("/api/v1/report_json", fields, self._api_key)
        data, evidence = self._decode("report-json", json.dumps(fields, sort_keys=True).encode(), response)
        if not isinstance(data, dict):
            raise IntegrationPolicyError("MobSF report JSON must be an object")
        return data, evidence

    def delete_scan(self, handle: MobSFScanHandle) -> MobSFEvidence:
        fields = {"hash": handle.scan_hash}
        response = self._transport.post_form("/api/v1/delete_scan", fields, self._api_key)
        _, evidence = self._decode("delete-scan", json.dumps(fields, sort_keys=True).encode(), response)
        return evidence

    def analyze(self, artifact: AuthorizedArtifact, cleanup: bool = True) -> MobSFAnalysisResult:
        handle, upload_ev = self.upload(artifact)
        evidence = [upload_ev]
        report: Mapping[str, Any] | None = None
        cleanup_completed = not cleanup
        primary_error: Exception | None = None
        try:
            evidence.append(self.scan(handle))
            report, report_ev = self.report_json(handle)
            evidence.append(report_ev)
        except Exception as exc:
            primary_error = exc
        finally:
            if cleanup:
                try:
                    evidence.append(self.delete_scan(handle))
                    cleanup_completed = True
                except Exception as cleanup_exc:
                    if primary_error is None:
                        raise IntegrationPolicyError("MobSF cleanup failed") from cleanup_exc
                    raise IntegrationPolicyError("MobSF analysis failed and cleanup also failed") from cleanup_exc
        if primary_error is not None:
            raise primary_error
        if report is None:
            raise IntegrationPolicyError("MobSF analysis produced no report")
        return MobSFAnalysisResult(handle, report, tuple(evidence), cleanup_completed)
