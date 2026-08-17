from __future__ import annotations

import hashlib
import ipaddress
import os
import shutil
import subprocess
import urllib.parse
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Callable

from .integration_http import IntegrationPolicyError


def _is_loopback(hostname: str) -> bool:
    host = hostname.strip().lower().rstrip(".")
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


@dataclass(frozen=True)
class MobSFLocalEndpoint:
    base_url: str

    def validate(self) -> "MobSFLocalEndpoint":
        parsed = urllib.parse.urlsplit(self.base_url)
        if parsed.scheme not in {"http", "https"}:
            raise IntegrationPolicyError("MobSF local API requires http or https")
        if parsed.username or parsed.password:
            raise IntegrationPolicyError("MobSF credentials must not be embedded in URLs")
        if not _is_loopback(parsed.hostname or ""):
            raise IntegrationPolicyError("MobSF artifact submission is restricted to loopback/local service")
        if parsed.path not in {"", "/"} or parsed.query or parsed.fragment:
            raise IntegrationPolicyError("MobSF base URL must not include path/query/fragment")
        return self


@dataclass(frozen=True)
class ToolProbeSpec:
    id: str
    executable_names: tuple[str, ...]
    version_args: tuple[str, ...]
    timeout_seconds: float = 5.0


@dataclass(frozen=True)
class ToolProbeResult:
    id: str
    status: str
    executable_path: str | None
    executable_sha256: str | None
    version_output: str | None
    observed_at: str
    error: str | None = None


class LocalToolProbe:
    """Discover/fingerprint local toolchains without installing or promoting them."""

    def __init__(self, which: Callable[[str], str | None] = shutil.which,
                 runner: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run) -> None:
        self._which = which
        self._runner = runner

    @staticmethod
    def _sha256_file(path: str) -> str:
        digest = hashlib.sha256()
        with open(path, "rb") as handle:
            for block in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(block)
        return digest.hexdigest()

    def probe(self, spec: ToolProbeSpec) -> ToolProbeResult:
        observed = datetime.now(timezone.utc).isoformat()
        path = next((p for n in spec.executable_names if (p := self._which(n))), None)
        if path is None:
            return ToolProbeResult(spec.id, "NOT_FOUND", None, None, None, observed)
        try:
            fingerprint = self._sha256_file(path)
            completed = self._runner(
                [path, *spec.version_args], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, timeout=spec.timeout_seconds, check=False, shell=False
            )
            output = (completed.stdout or "").strip()[:16384]
            status = "AVAILABLE" if completed.returncode == 0 else "VERSION_PROBE_FAILED"
            return ToolProbeResult(
                spec.id, status, os.path.realpath(path), fingerprint, output, observed,
                None if status == "AVAILABLE" else f"exit={completed.returncode}"
            )
        except (OSError, subprocess.SubprocessError) as exc:
            return ToolProbeResult(
                spec.id, "PROBE_ERROR", os.path.realpath(path), None, None, observed,
                f"{type(exc).__name__}: {exc}"
            )


MOBILE_TOOL_PROBES = (
    ToolProbeSpec("jadx", ("jadx", "jadx.bat"), ("--version",)),
    ToolProbeSpec("apktool", ("apktool", "apktool.bat"), ("--version",)),
    ToolProbeSpec("adb", ("adb", "adb.exe"), ("version",)),
    ToolProbeSpec("apkanalyzer", ("apkanalyzer", "apkanalyzer.bat"), ("--version",)),
    ToolProbeSpec("bundletool", ("bundletool", "bundletool.bat"), ("version",)),
    ToolProbeSpec("frida", ("frida", "frida.exe"), ("--version",)),
    ToolProbeSpec("mobsfscan", ("mobsfscan", "mobsfscan.exe"), ("--version",)),
)


@dataclass(frozen=True)
class ToolSource:
    id: str
    github_owner: str
    github_repo: str
    role: str
    execution_mode: str


TOOL_SOURCES = (
    ToolSource("jadx", "skylot", "jadx", "DEX/APK/AAB code comprehension", "out-of-process"),
    ToolSource("apktool", "iBotPeaches", "Apktool", "Android resources/manifest decoding", "out-of-process"),
    ToolSource("mobsf", "MobSF", "Mobile-Security-Framework-MobSF", "security cross-check", "local-service"),
    ToolSource("frida", "frida", "frida", "authorized runtime tracing", "observation-only"),
)


class InstrumentationObservationPolicy:
    """Fail-closed boundary for Frida/equivalent runtime instrumentation."""

    ALLOWED_OPERATIONS = frozenset({
        "enumerate_devices", "enumerate_processes", "enumerate_modules",
        "attach_observe", "trace_calls", "trace_blocks", "read_metadata",
    })
    FORBIDDEN_TERMS = (
        "bypass", "disable", "patch", "replace", "write_memory", "set_argument",
        "set_return", "pinning", "certificate", "authentication", "mfa", "captcha",
        "signature", "anti_tamper", "integrity", "root_detection", "jailbreak_detection",
    )

    @classmethod
    def validate(cls, operation: str, script_text: str = "") -> None:
        normalized = operation.strip().lower()
        if normalized not in cls.ALLOWED_OPERATIONS:
            raise IntegrationPolicyError(f"instrumentation operation is not observation-only: {operation}")
        lowered = script_text.lower()
        matches = sorted({term for term in cls.FORBIDDEN_TERMS if term in lowered})
        if matches:
            raise IntegrationPolicyError(
                "instrumentation script violates observation-only policy: " + ", ".join(matches)
            )


@dataclass(frozen=True)
class IntegrationCandidate:
    id: str
    family: str
    tasks: tuple[str, ...]
    default_mode: str
    data_boundary: str
    benchmark_required: bool = True


INTEGRATION_CANDIDATES = (
    IntegrationCandidate(
        "github-public-intelligence-api", "repository-intelligence",
        ("repository-metadata", "release-discovery", "workflow-artifact-evidence", "global-advisories", "spdx-sbom"),
        "read-only-https", "metadata-only"
    ),
    IntegrationCandidate(
        "osv-api", "vulnerability-intelligence",
        ("package-vulnerability-query", "commit-vulnerability-query", "batch-cross-check"),
        "read-only-https", "metadata-only"
    ),
    IntegrationCandidate(
        "mobsf-rest-local", "mobile-security-analysis",
        ("static-security-cross-check", "dynamic-security-cross-check"),
        "loopback-service", "artifact-local-only"
    ),
    IntegrationCandidate(
        "androguard", "android-static-analysis",
        ("apk-parser", "dex-analysis", "call-graph", "cfg", "certificate-analysis"),
        "local-library", "artifact-local-only"
    ),
    IntegrationCandidate(
        "android-apkanalyzer", "android-artifact-analysis",
        ("manifest", "dex", "resources", "apk-diff"),
        "local-sdk-tool", "artifact-local-only"
    ),
    IntegrationCandidate(
        "frida-observation-only", "runtime-instrumentation",
        ("call-tracing", "module-observation", "execution-trace"),
        "authorized-observation-only", "artifact-local-only"
    ),
)


def integration_inventory() -> dict[str, Any]:
    return {
        "schema_version": 1,
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "candidates": [
            {
                "id": c.id, "family": c.family, "tasks": list(c.tasks),
                "default_mode": c.default_mode, "data_boundary": c.data_boundary,
                "benchmark_required": c.benchmark_required,
            }
            for c in INTEGRATION_CANDIDATES
        ],
        "tool_sources": [
            {
                "id": s.id, "github": f"{s.github_owner}/{s.github_repo}",
                "role": s.role, "execution_mode": s.execution_mode,
            }
            for s in TOOL_SOURCES
        ],
    }
