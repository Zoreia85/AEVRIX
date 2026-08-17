from __future__ import annotations

import hashlib
import importlib.metadata
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable

from .integration_tools import IntegrationCandidate, ToolProbeSpec, ToolSource
from .tool_invocation import CapabilityInvocationPlan, InvocationPolicyError

_SHA256 = re.compile(r"^[0-9a-f]{64}$")


@dataclass(frozen=True)
class AuthorizedDerivedArtifact:
    path: str
    sha256: str
    source_sha256: str
    authorized: bool
    kind: str

    def validate(self) -> "AuthorizedDerivedArtifact":
        if not self.authorized:
            raise PermissionError("derived artifact analysis requires explicit authorization")
        if not _SHA256.fullmatch(self.sha256.lower()):
            raise ValueError("derived artifact sha256 must be 64 hexadecimal characters")
        if not _SHA256.fullmatch(self.source_sha256.lower()):
            raise ValueError("source artifact sha256 must be 64 hexadecimal characters")
        if self.kind not in {"apk", "elf", "macho", "dex", "native-library"}:
            raise ValueError("unsupported derived artifact kind")
        if not self.path.strip():
            raise ValueError("derived artifact path is required")
        return self


def _safe_output(root: str, child: str) -> str:
    base = Path(root).resolve()
    target = (base / child).resolve()
    try:
        target.relative_to(base)
    except ValueError as exc:
        raise InvocationPolicyError("native-analysis output escapes workspace") from exc
    return str(target)


class ApkidPlans:
    @staticmethod
    def scan(artifact_path: str, sha256: str, source_sha256: str,
             kind: str = "dex", authorized: bool = True) -> CapabilityInvocationPlan:
        if kind not in {"apk", "dex"}:
            raise InvocationPolicyError("APKiD plan requires APK or DEX evidence")
        artifact = AuthorizedDerivedArtifact(
            artifact_path, sha256, source_sha256, authorized, kind
        ).validate()
        return CapabilityInvocationPlan(
            capability_id="apkid",
            argv=("apkid", "-j", artifact.path),
            input_sha256=artifact.sha256.lower(),
            output_path=None,
            network_mode="offline",
            target_mutation=False,
            evidence_kind="packer-obfuscator-fingerprint-json",
            authorized=True,
        ).validate()


class GhidraPlans:
    @staticmethod
    def headless(artifact: AuthorizedDerivedArtifact, workspace: str,
                 timeout_seconds: int = 180, max_cpu: int = 2) -> CapabilityInvocationPlan:
        artifact.validate()
        if artifact.kind not in {"elf", "macho", "native-library"}:
            raise InvocationPolicyError("Ghidra native plan requires ELF/Mach-O/native library evidence")
        if not 5 <= timeout_seconds <= 3600:
            raise ValueError("Ghidra per-file timeout must be 5-3600 seconds")
        if not 1 <= max_cpu <= 8:
            raise ValueError("Ghidra max_cpu must be 1-8")
        project_root = _safe_output(workspace, "ghidra-projects")
        project_name = "aevrix-" + artifact.sha256[:12]
        return CapabilityInvocationPlan(
            capability_id="ghidra-headless",
            argv=(
                "analyzeHeadless", project_root, project_name,
                "-import", artifact.path,
                "-readOnly", "-deleteProject",
                "-analysisTimeoutPerFile", str(timeout_seconds),
                "-max-cpu", str(max_cpu),
            ),
            input_sha256=artifact.sha256.lower(),
            output_path=project_root,
            network_mode="offline",
            target_mutation=False,
            evidence_kind="native-code-analysis-project-ephemeral",
            authorized=True,
        ).validate()


@dataclass(frozen=True)
class PythonDistributionEvidence:
    distribution: str
    status: str
    version: str | None
    observed_at: str


class PythonDistributionProbe:
    """Checks installed Python distribution metadata without importing candidate code."""

    def __init__(self, version_resolver: Callable[[str], str] = importlib.metadata.version) -> None:
        self._version = version_resolver

    def probe(self, distribution: str) -> PythonDistributionEvidence:
        observed = datetime.now(timezone.utc).isoformat()
        try:
            version = self._version(distribution)
            return PythonDistributionEvidence(distribution, "AVAILABLE", version, observed)
        except importlib.metadata.PackageNotFoundError:
            return PythonDistributionEvidence(distribution, "NOT_FOUND", None, observed)
        except Exception:
            return PythonDistributionEvidence(distribution, "PROBE_ERROR", None, observed)


NATIVE_TOOL_SOURCES = (
    ToolSource("ghidra-headless", "NationalSecurityAgency", "ghidra", "ELF/Mach-O/JNI native analysis", "out-of-process"),
    ToolSource("apkid", "rednaga", "APKiD", "packer/compiler/obfuscator fingerprinting", "out-of-process"),
    ToolSource("appium-webdriver-lab", "appium", "appium", "cross-platform UI automation comparison", "local-service"),
)

NATIVE_TOOL_PROBES = (
    ToolProbeSpec("apkid", ("apkid", "apkid.exe"), ("--help",)),
    ToolProbeSpec("appium-webdriver-lab", ("appium", "appium.cmd"), ("--version",)),
)

NATIVE_INTEGRATION_CANDIDATES = (
    IntegrationCandidate(
        "ghidra-headless", "native-binary-analysis",
        ("elf-analysis", "macho-analysis", "jni-native-code-analysis"),
        "local-headless-tool", "artifact-local-only"
    ),
    IntegrationCandidate(
        "apkid", "android-protection-fingerprinting",
        ("compiler-fingerprint", "packer-detection", "obfuscator-detection", "rasp-fingerprint"),
        "local-cli", "artifact-local-only"
    ),
    IntegrationCandidate(
        "lief", "binary-format-parsing",
        ("elf-parser", "macho-parser", "dex-oat-vdex-parser", "binary-metadata-cross-check"),
        "local-library", "artifact-local-only"
    ),
    IntegrationCandidate(
        "appium-webdriver-lab", "cross-platform-ui-automation",
        ("android-ui-automation-comparison", "ios-ui-automation-comparison", "hybrid-app-observation"),
        "local-service", "artifact-and-device-local-only"
    ),
)


def native_toolchain_inventory() -> dict:
    return {
        "schema_version": 1,
        "candidates": [
            {
                "id": c.id,
                "family": c.family,
                "tasks": list(c.tasks),
                "default_mode": c.default_mode,
                "data_boundary": c.data_boundary,
                "benchmark_required": c.benchmark_required,
            }
            for c in NATIVE_INTEGRATION_CANDIDATES
        ],
        "tool_sources": [
            {
                "id": s.id,
                "github": f"{s.github_owner}/{s.github_repo}",
                "role": s.role,
                "execution_mode": s.execution_mode,
            }
            for s in NATIVE_TOOL_SOURCES
        ],
    }
