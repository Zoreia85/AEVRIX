from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Mapping

from .integration_tools import IntegrationCandidate, ToolProbeSpec
from .tool_invocation import AuthorizedArtifact, CapabilityInvocationPlan, InvocationPolicyError

_CERT_SHA256 = re.compile(r"(?im)^Signer #(?P<signer>\d+) certificate SHA-256 digest:\s*(?P<digest>[0-9a-f:]{64,95})\s*$")
_SCHEME = re.compile(r"(?im)^Verified using v(?P<version>[1-4]) scheme.*?:\s*(?P<value>true|false)\s*$")
_VERIFIED = re.compile(r"(?im)^Verifies\s*$")


class ApkSignerPlans:
    @staticmethod
    def verify(artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        artifact.validate()
        if artifact.kind != "apk":
            raise InvocationPolicyError("apksigner verification requires APK input")
        return CapabilityInvocationPlan(
            capability_id="android-apksigner-verify",
            argv=("apksigner", "verify", "--verbose", "--print-certs", artifact.path),
            input_sha256=artifact.sha256.lower(),
            output_path=None,
            network_mode="offline",
            target_mutation=False,
            evidence_kind="apk-signature-verification-certificates",
            authorized=True,
        ).validate()


class BundletoolPlans:
    @staticmethod
    def _dump(artifact: AuthorizedArtifact, target: str) -> CapabilityInvocationPlan:
        artifact.validate()
        if artifact.kind != "aab":
            raise InvocationPolicyError("bundletool dump requires AAB input")
        if target not in {"manifest", "resources", "config", "runtime-enabled-sdk-config"}:
            raise InvocationPolicyError("unsupported bundletool dump target")
        return CapabilityInvocationPlan(
            capability_id="android-bundletool-readonly",
            argv=("bundletool", "dump", target, f"--bundle={artifact.path}"),
            input_sha256=artifact.sha256.lower(),
            output_path=None,
            network_mode="offline",
            target_mutation=False,
            evidence_kind=f"aab-{target}",
            authorized=True,
        ).validate()

    @classmethod
    def manifest(cls, artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        return cls._dump(artifact, "manifest")

    @classmethod
    def resources(cls, artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        return cls._dump(artifact, "resources")

    @classmethod
    def config(cls, artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        return cls._dump(artifact, "config")

    @classmethod
    def runtime_enabled_sdk_config(cls, artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        return cls._dump(artifact, "runtime-enabled-sdk-config")


@dataclass(frozen=True)
class ApkSignatureEvidence:
    overall_verified: bool | None
    schemes: Mapping[str, bool]
    signer_certificate_sha256: tuple[str, ...]
    parser_status: str


def parse_apksigner_verify_output(output: str, process_succeeded: bool) -> ApkSignatureEvidence:
    schemes = {
        f"v{match.group('version')}": match.group("value").lower() == "true"
        for match in _SCHEME.finditer(output)
    }
    digests: list[str] = []
    for match in _CERT_SHA256.finditer(output):
        normalized = match.group("digest").replace(":", "").lower()
        if len(normalized) == 64 and all(char in "0123456789abcdef" for char in normalized):
            digests.append(normalized)
    if not process_succeeded:
        overall: bool | None = False
        status = "PROCESS_FAILED"
    elif _VERIFIED.search(output) or any(schemes.values()):
        overall = True
        status = "PARSED"
    else:
        overall = None
        status = "UNMEASURED_OUTPUT_FORMAT"
    return ApkSignatureEvidence(overall, schemes, tuple(digests), status)


ANDROID_INTEGRITY_TOOL_PROBES = (
    ToolProbeSpec(
        "android-apksigner-verify",
        ("apksigner", "apksigner.bat"),
        ("--version",),
    ),
)

ANDROID_INTEGRITY_CANDIDATES = (
    IntegrationCandidate(
        "android-apksigner-verify",
        "android-package-integrity",
        ("apk-signature-verification", "signer-certificate-fingerprints", "signature-scheme-evidence"),
        "local-sdk-tool",
        "artifact-local-only",
    ),
    IntegrationCandidate(
        "android-bundletool-readonly",
        "android-app-bundle-analysis",
        ("aab-manifest", "aab-resources", "aab-config", "runtime-enabled-sdk-config"),
        "local-vendor-tool",
        "artifact-local-only",
    ),
)
