from __future__ import annotations

import hashlib
import os
import plistlib
import zipfile
from pathlib import Path, PurePosixPath
from typing import Iterable

from .models import ArchiveSafety, ArtifactReport, EvidenceItem

MAX_ARCHIVE_ENTRIES = 50_000
MAX_TOTAL_UNCOMPRESSED = 2 * 1024 * 1024 * 1024
MAX_COMPRESSION_RATIO = 250.0
MAX_PLIST_BYTES = 4 * 1024 * 1024


def sha256_file(path: Path, chunk_size: int = 1024 * 1024) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(chunk_size):
            digest.update(chunk)
    return digest.hexdigest()


def _path_is_unsafe(name: str) -> bool:
    normalized = name.replace("\\", "/")
    p = PurePosixPath(normalized)
    return p.is_absolute() or ".." in p.parts or normalized.startswith("/")


def _archive_safety(infos: Iterable[zipfile.ZipInfo]) -> ArchiveSafety:
    info_list = list(infos)
    unsafe_paths: list[str] = []
    total_uncompressed = 0
    max_ratio = 0.0

    for info in info_list:
        if _path_is_unsafe(info.filename):
            unsafe_paths.append(info.filename)
        total_uncompressed += max(0, info.file_size)
        if info.file_size:
            compressed = max(1, info.compress_size)
            max_ratio = max(max_ratio, info.file_size / compressed)

    violations: list[str] = []
    if len(info_list) > MAX_ARCHIVE_ENTRIES:
        violations.append("archive_entry_limit_exceeded")
    if total_uncompressed > MAX_TOTAL_UNCOMPRESSED:
        violations.append("archive_uncompressed_size_limit_exceeded")
    if max_ratio > MAX_COMPRESSION_RATIO:
        violations.append("archive_compression_ratio_limit_exceeded")

    return ArchiveSafety(
        entry_count=len(info_list),
        total_uncompressed_bytes=total_uncompressed,
        max_compression_ratio=round(max_ratio, 3),
        unsafe_paths=tuple(sorted(set(unsafe_paths))),
        policy_violations=tuple(violations),
    )


def _detect_zip_format(names: set[str], suffix: str) -> tuple[str, str, float, list[EvidenceItem]]:
    evidence: list[EvidenceItem] = []

    has_android_manifest = "AndroidManifest.xml" in names
    has_dex = any(name.startswith("classes") and name.endswith(".dex") for name in names)
    has_resources = "resources.arsc" in names
    if has_android_manifest and (has_dex or has_resources):
        evidence.append(EvidenceItem("archive_marker", "AndroidManifest.xml", 1.0, "zip_inventory"))
        if has_dex:
            evidence.append(EvidenceItem("archive_marker", "classes*.dex", 1.0, "zip_inventory"))
        return "APK", "android", 0.99, evidence

    if {"BundleConfig.pb", "base/manifest/AndroidManifest.xml"}.issubset(names) or (
        "BundleConfig.pb" in names and any(n.startswith("base/") for n in names)
    ):
        evidence.append(EvidenceItem("archive_marker", "BundleConfig.pb", 1.0, "zip_inventory"))
        return "AAB", "android", 0.99, evidence

    apk_entries = [n for n in names if n.lower().endswith(".apk")]
    if apk_entries and ("manifest.json" in names or suffix == ".xapk"):
        evidence.append(EvidenceItem("nested_artifact", f"{len(apk_entries)} apk entries", 0.95, "zip_inventory"))
        return "XAPK", "android", 0.95, evidence

    ipa_plists = [n for n in names if n.startswith("Payload/") and ".app/" in n and n.endswith("/Info.plist")]
    if ipa_plists:
        evidence.append(EvidenceItem("archive_marker", ipa_plists[0], 1.0, "zip_inventory"))
        return "IPA", "ios", 0.99, evidence

    if suffix in {".apk", ".aab", ".xapk", ".ipa"}:
        evidence.append(EvidenceItem("extension_hint", suffix, 0.35, "filename"))
        return suffix[1:].upper(), "android" if suffix != ".ipa" else "ios", 0.35, evidence

    return "ZIP", "unknown", 0.5, [EvidenceItem("container", "zip", 1.0, "magic")]


def _read_ios_metadata(zf: zipfile.ZipFile, names: set[str]) -> dict[str, str]:
    plist_names = [n for n in names if n.startswith("Payload/") and ".app/" in n and n.endswith("/Info.plist")]
    if not plist_names:
        return {}

    info = zf.getinfo(sorted(plist_names)[0])
    if info.file_size > MAX_PLIST_BYTES or info.flag_bits & 0x1:
        return {}

    try:
        parsed = plistlib.loads(zf.read(info))
    except (OSError, ValueError, plistlib.InvalidFileException):
        return {}

    allowed = (
        "CFBundleIdentifier",
        "CFBundleExecutable",
        "CFBundleShortVersionString",
        "CFBundleVersion",
        "MinimumOSVersion",
        "UIDeviceFamily",
    )
    metadata: dict[str, str] = {}
    for key in allowed:
        value = parsed.get(key)
        if isinstance(value, (str, int, float, bool)):
            metadata[key] = str(value)
        elif isinstance(value, list) and all(isinstance(item, (str, int)) for item in value):
            metadata[key] = ",".join(str(item) for item in value)
    return metadata


def inspect_artifact(path_like: str | os.PathLike[str]) -> ArtifactReport:
    path = Path(path_like)
    if not path.is_file():
        raise FileNotFoundError(path)

    size = path.stat().st_size
    sha256 = sha256_file(path)
    suffix = path.suffix.lower()

    if zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as zf:
            infos = zf.infolist()
            safety = _archive_safety(infos)
            names = {info.filename.replace("\\", "/") for info in infos}
            fmt, platform, confidence, evidence = _detect_zip_format(names, suffix)
            metadata: dict[str, str] = {}
            if fmt == "IPA" and safety.safe_for_inventory:
                metadata = _read_ios_metadata(zf, names)

        return ArtifactReport(
            schema_version=1,
            artifact_name=path.name,
            artifact_size_bytes=size,
            sha256=sha256,
            format=fmt,
            platform=platform,
            confidence=confidence,
            evidence=tuple(evidence),
            archive_safety=safety,
            metadata=metadata,
        )

    with path.open("rb") as handle:
        header = handle.read(8)
    evidence: list[EvidenceItem] = []
    fmt = "UNKNOWN"
    platform = "unknown"
    confidence = 0.0

    if header.startswith(b"dex\n"):
        fmt, platform, confidence = "DEX", "android", 0.99
        evidence.append(EvidenceItem("magic", "dex", 1.0, "file_header"))
    elif suffix in {".apk", ".aab", ".xapk", ".ipa"}:
        fmt = suffix[1:].upper()
        platform = "ios" if suffix == ".ipa" else "android"
        confidence = 0.2
        evidence.append(EvidenceItem("extension_hint", suffix, 0.2, "filename"))

    return ArtifactReport(
        schema_version=1,
        artifact_name=path.name,
        artifact_size_bytes=size,
        sha256=sha256,
        format=fmt,
        platform=platform,
        confidence=confidence,
        evidence=tuple(evidence),
    )
