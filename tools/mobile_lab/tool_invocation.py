from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from pathlib import Path


class InvocationPolicyError(RuntimeError):
    pass


_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_SERIAL = re.compile(r"^[A-Za-z0-9_.:-]{1,128}$")


@dataclass(frozen=True)
class AuthorizedArtifact:
    path: str
    sha256: str
    authorized: bool
    kind: str

    def validate(self) -> "AuthorizedArtifact":
        if not self.authorized:
            raise PermissionError("artifact analysis requires explicit authorization")
        if not _SHA256.fullmatch(self.sha256.lower()):
            raise ValueError("artifact sha256 must be 64 hexadecimal characters")
        if self.kind not in {"apk", "aab", "xapk", "ipa"}:
            raise ValueError("unsupported artifact kind")
        if not self.path.strip():
            raise ValueError("artifact path is required")
        return self


@dataclass(frozen=True)
class CapabilityInvocationPlan:
    capability_id: str
    argv: tuple[str, ...]
    input_sha256: str | None
    output_path: str | None
    network_mode: str
    target_mutation: bool
    evidence_kind: str
    authorized: bool

    def validate(self) -> "CapabilityInvocationPlan":
        if not self.authorized:
            raise PermissionError("capability invocation plan is not authorized")
        if not self.argv or not self.argv[0].strip():
            raise InvocationPolicyError("argv must include an executable")
        if any("\x00" in part for part in self.argv):
            raise InvocationPolicyError("argv contains NUL")
        if self.network_mode not in {"offline", "loopback", "controlled"}:
            raise InvocationPolicyError("unsupported network mode")
        return self

    @property
    def command_sha256(self) -> str:
        material = b"\0".join(part.encode() for part in self.argv)
        return hashlib.sha256(material).hexdigest()


def _safe_output(root: str, child: str) -> str:
    base = Path(root).resolve()
    target = (base / child).resolve()
    try:
        target.relative_to(base)
    except ValueError as exc:
        raise InvocationPolicyError("output path escapes benchmark workspace") from exc
    return str(target)


class ApkAnalyzerPlans:
    """Official Android SDK apkanalyzer read-only inspection plans."""

    @staticmethod
    def _plan(artifact: AuthorizedArtifact, subject: str, verb: str,
              evidence_kind: str) -> CapabilityInvocationPlan:
        artifact.validate()
        if artifact.kind != "apk":
            raise InvocationPolicyError("apkanalyzer plans currently require APK input")
        return CapabilityInvocationPlan(
            "android-apkanalyzer", ("apkanalyzer", subject, verb, artifact.path),
            artifact.sha256.lower(), None, "offline", False, evidence_kind, True
        ).validate()

    @classmethod
    def summary(cls, artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        return cls._plan(artifact, "apk", "summary", "apk-summary")

    @classmethod
    def manifest(cls, artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        return cls._plan(artifact, "manifest", "print", "manifest-xml")

    @classmethod
    def permissions(cls, artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        return cls._plan(artifact, "manifest", "permissions", "permissions")

    @classmethod
    def dex_list(cls, artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        return cls._plan(artifact, "dex", "list", "dex-list")

    @classmethod
    def files_list(cls, artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        return cls._plan(artifact, "files", "list", "file-inventory")


class JadxPlans:
    @staticmethod
    def decompile(artifact: AuthorizedArtifact, output_root: str) -> CapabilityInvocationPlan:
        artifact.validate()
        if artifact.kind not in {"apk", "aab", "xapk"}:
            raise InvocationPolicyError("JADX plan requires Android artifact")
        output = _safe_output(output_root, f"jadx-{artifact.sha256[:12]}")
        return CapabilityInvocationPlan(
            "jadx", ("jadx", "--output-dir", output, artifact.path),
            artifact.sha256.lower(), output, "offline", False, "decompiled-code-resources", True
        ).validate()


class ApktoolPlans:
    @staticmethod
    def decode(artifact: AuthorizedArtifact, output_root: str) -> CapabilityInvocationPlan:
        artifact.validate()
        if artifact.kind != "apk":
            raise InvocationPolicyError("Apktool plan requires APK input")
        output = _safe_output(output_root, f"apktool-{artifact.sha256[:12]}")
        return CapabilityInvocationPlan(
            "apktool", ("apktool", "d", "-f", "-o", output, artifact.path),
            artifact.sha256.lower(), output, "offline", False, "decoded-resources-smali", True
        ).validate()


class AndroguardPlans:
    @staticmethod
    def manifest(artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        artifact.validate()
        if artifact.kind != "apk":
            raise InvocationPolicyError("Androguard plan requires APK input")
        return CapabilityInvocationPlan(
            "androguard", ("androguard", "axml", artifact.path), artifact.sha256.lower(),
            None, "offline", False, "manifest-xml", True
        ).validate()

    @staticmethod
    def signatures(artifact: AuthorizedArtifact) -> CapabilityInvocationPlan:
        artifact.validate()
        if artifact.kind != "apk":
            raise InvocationPolicyError("Androguard plan requires APK input")
        return CapabilityInvocationPlan(
            "androguard", ("androguard", "sign", artifact.path), artifact.sha256.lower(),
            None, "offline", False, "certificate-fingerprints", True
        ).validate()


class AdbObservationPlans:
    """Read-only runtime evidence plans for an already-created disposable Android environment."""

    @staticmethod
    def _serial(serial: str) -> str:
        if not _SERIAL.fullmatch(serial):
            raise InvocationPolicyError("invalid adb serial")
        return serial

    @classmethod
    def devices(cls) -> CapabilityInvocationPlan:
        return CapabilityInvocationPlan(
            "android-sdk-dynamic-lab", ("adb", "devices", "-l"), None, None,
            "offline", False, "device-inventory", True
        ).validate()

    @classmethod
    def getprop(cls, serial: str) -> CapabilityInvocationPlan:
        serial = cls._serial(serial)
        return CapabilityInvocationPlan(
            "android-sdk-dynamic-lab", ("adb", "-s", serial, "shell", "getprop"), None, None,
            "offline", False, "device-properties", True
        ).validate()

    @classmethod
    def logcat_snapshot(cls, serial: str) -> CapabilityInvocationPlan:
        serial = cls._serial(serial)
        return CapabilityInvocationPlan(
            "android-sdk-dynamic-lab", ("adb", "-s", serial, "logcat", "-d", "-v", "threadtime"),
            None, None, "offline", False, "logcat-snapshot", True
        ).validate()

    @classmethod
    def screenshot(cls, serial: str) -> CapabilityInvocationPlan:
        serial = cls._serial(serial)
        return CapabilityInvocationPlan(
            "android-sdk-dynamic-lab", ("adb", "-s", serial, "exec-out", "screencap", "-p"),
            None, None, "offline", False, "screenshot-png", True
        ).validate()

    @classmethod
    def activity_state(cls, serial: str) -> CapabilityInvocationPlan:
        serial = cls._serial(serial)
        return CapabilityInvocationPlan(
            "android-sdk-dynamic-lab", ("adb", "-s", serial, "shell", "dumpsys", "activity", "activities"),
            None, None, "offline", False, "activity-state", True
        ).validate()
