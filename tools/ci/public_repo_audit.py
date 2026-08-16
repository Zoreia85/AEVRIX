#!/usr/bin/env python3
from __future__ import annotations

import ast
import hashlib
import json
import re
from pathlib import Path

from repository_intelligence_audit import audit_registry

ROOT = Path(__file__).resolve().parents[2]
TEXT_SUFFIXES = {".cs", ".go", ".mod", ".sum", ".py", ".ps1", ".json", ".md", ".yml", ".yaml", ".xml", ".csproj", ".props", ".wxs"}
IGNORED_PARTS = {".git", "bin", "obj", "artifacts", "__pycache__", ".pytest_cache"}

FORBIDDEN_BRANDING = re.compile("(?i)(" + "grupo" + "[ -]?" + "temper|" + "temper" + "researchstudio|" + "temper" + " research studio)")
PRIVATE_KEY = re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")
LIKELY_SECRET = re.compile(r"(?im)^\s*(?:(?:const|static|readonly|var|string)\s+)*(?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|password)\s*=\s*[\"'][^\"']{12,}[\"']")
JSON_SECRET = re.compile(r"(?im)^\s*[\"](?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|password)[\"]\s*:\s*[\"][^\"]{12,}[\"]")
DIRECT_HTTP = re.compile(r"\bnew\s+HttpClient\s*\(")
PATCH_QUEUE_GROUP = "group: aevrix-bot-patch-authoritative"
TOKEN_PATTERNS = {
    "GitHub token": re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b"),
    "AWS access key": re.compile(r"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
    "Google API key": re.compile(r"\bAIza[0-9A-Za-z_-]{35}\b"),
    "Slack token": re.compile(r"\bxox[baprs]-[0-9A-Za-z-]{20,}\b"),
    "GitLab token": re.compile(r"\bglpat-[0-9A-Za-z_-]{20,}\b"),
    "generic bearer token": re.compile(r"(?i)\bAuthorization\s*:\s*Bearer\s+[A-Za-z0-9._~+/=-]{20,}"),
}
URL_WITH_CREDENTIALS = re.compile(r"(?i)\bhttps?://([^/\s:@]+):([^/\s@]+)@([^/\s]+)")
WINDOWS_USER_PATH = re.compile(r"(?i)\b[A-Z]:\\Users\\([^\\/\r\n]+)")
POSIX_USER_PATH = re.compile(r"(?i)(?:^|[\s\"'])/(?:Users|home)/([^/\s\"']+)")
EMAIL_ADDRESS = re.compile(r"(?i)\b[A-Z0-9._%+-]+@([A-Z0-9.-]+\.[A-Z]{2,})\b")
ALLOWED_EMAIL_DOMAINS = {"example.com", "example.org", "example.net", "users.noreply.github.com"}
ALLOWED_TECHNICAL_EMAILS = {"noreply@github.com"}
GENERIC_PATH_USERS = {"user", "users", "username", "runner", "runneradmin", "developer", "dev", "test", "tester"}
SENSITIVE_FILE_NAMES = {
    ".env", ".env.local", ".env.production", "credentials.json", "credentials.yml",
    "credentials.yaml", "cookies.txt", "session.json", "id_rsa", "id_ed25519",
}
SENSITIVE_SUFFIXES = {".pfx", ".p12", ".key"}


def source_files():
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.name == "public-source-audit.json" or any(part in IGNORED_PARTS for part in path.parts):
            continue
        if path.suffix.lower() in TEXT_SUFFIXES or path.name in {"LICENSE", "NOTICE", ".gitignore", ".editorconfig"}:
            yield path


def rel(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def audit_sensitive_filename(path: Path, failures: list[str]) -> None:
    name = path.name.lower()
    if name in SENSITIVE_FILE_NAMES or path.suffix.lower() in SENSITIVE_SUFFIXES:
        failures.append(f"secret-bearing filename is forbidden: {rel(path)}")


def is_reserved_test_host(host: str) -> bool:
    normalized = host.lower().split(":", 1)[0].rstrip(".")
    return normalized in {"example.com", "example.org", "example.net"} or normalized.endswith(".example")


def audit_public_identity(text: str, path: Path, failures: list[str]) -> None:
    for match in EMAIL_ADDRESS.finditer(text):
        address = match.group(0).lower()
        domain = match.group(1).lower()
        if address not in ALLOWED_TECHNICAL_EMAILS and domain not in ALLOWED_EMAIL_DOMAINS and not domain.endswith(".example"):
            failures.append(f"personal/non-synthetic e-mail address found: {rel(path)}")
            break
    for pattern in (WINDOWS_USER_PATH, POSIX_USER_PATH):
        match = pattern.search(text)
        if match and match.group(1).lower() not in GENERIC_PATH_USERS:
            failures.append(f"local user/workstation path found: {rel(path)}")
            break


def audit_credential_material(text: str, path: Path, failures: list[str]) -> None:
    for label, pattern in TOKEN_PATTERNS.items():
        if pattern.search(text):
            failures.append(f"{label} material found: {rel(path)}")
    for match in URL_WITH_CREDENTIALS.finditer(text):
        if not is_reserved_test_host(match.group(3)):
            failures.append(f"URL containing embedded credentials found: {rel(path)}")
            break


def audit_patch_queue_policy(failures: list[str]) -> None:
    workflows = ROOT / ".github" / "workflows"
    legacy = (workflows / "bot-patch.yml").read_text(encoding="utf-8")
    authoritative = (workflows / "bot-patch-v2.yml").read_text(encoding="utf-8")
    marker_cleanup = (workflows / "bot-patch-v3.yml").read_text(encoding="utf-8")

    if "issues:" in legacy or "contents: write" in legacy:
        failures.append("legacy patch processor must remain issue-disabled and read-only")
    if PATCH_QUEUE_GROUP not in authoritative or PATCH_QUEUE_GROUP not in marker_cleanup:
        failures.append("patch queue and marker cleanup must share one repository-wide concurrency group")
    if "rm -rf .aevrix/queue" not in authoritative:
        failures.append("authoritative patch queue must remove stale queue markers before promotion")
    if "git push --force origin" in marker_cleanup:
        failures.append("marker cleanup must never perform an unconditional force push")
    if "git push --force-with-lease=main:${GITHUB_SHA}" not in marker_cleanup:
        failures.append("marker cleanup must use an exact main-tip force-with-lease")
    if 'if [ "$ORIGIN_MAIN" != "$GITHUB_SHA" ]' not in marker_cleanup:
        failures.append("marker cleanup must verify that it still owns the main tip before restoring")
    if "steps.discover" in marker_cleanup or "AEVRIX-PATCH-V1" in marker_cleanup:
        failures.append("marker cleanup must not process patch payloads")


def audit_privacy_root_policy(failures: list[str]) -> None:
    workflow = (ROOT / ".github" / "workflows" / "privacy-root-rewrite.yml").read_text(encoding="utf-8")

    if "branches: [main]" not in workflow:
        failures.append("privacy canonicalization must monitor pushes to main")
    if "paths:" in workflow:
        failures.append("privacy canonicalization must not be path-filtered")
    if "github.actor != 'github-actions[bot]'" not in workflow:
        failures.append("privacy canonicalization must skip bot-authored canonical commits")
    if 'git config user.name "github-actions[bot]"' not in workflow or 'git config user.email "41898282+github-actions[bot]@users.noreply.github.com"' not in workflow:
        failures.append("privacy canonicalization must commit with bot/noreply identity")
    if "python3 tools/ci/public_repo_audit.py" not in workflow:
        failures.append("privacy canonicalization must audit the public tree before changing main")
    if "git checkout --orphan" in workflow:
        failures.append("privacy canonicalization must preserve canonical ancestry and must not create orphan roots")
    if "git commit-tree" not in workflow or "canonical_parent" not in workflow:
        failures.append("privacy canonicalization must replace non-canonical history using an explicit canonical parent")
    if "git push --force-with-lease=main:${AEVRIX_PRIVACY_LEASE_SHA}" not in workflow:
        failures.append("privacy canonicalization must use an exact main-tip force-with-lease")
    if "no canonical bot/noreply ancestor exists" not in workflow:
        failures.append("privacy canonicalization must fail closed when no canonical ancestor exists")
    if PATCH_QUEUE_GROUP not in workflow:
        failures.append("privacy canonicalization and patch queue must share the authoritative writer lock")


def main() -> int:
    failures: list[str] = []
    warnings: list[str] = []
    files = list(source_files())

    for path in files:
        audit_sensitive_filename(path, failures)
        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            failures.append(f"non-UTF8 text file: {rel(path)}")
            continue

        if FORBIDDEN_BRANDING.search(text):
            failures.append(f"legacy product/organization branding found: {rel(path)}")
        if PRIVATE_KEY.search(text):
            failures.append(f"private key material found: {rel(path)}")
        if LIKELY_SECRET.search(text) or JSON_SECRET.search(text):
            failures.append(f"likely hard-coded secret found: {rel(path)}")
        audit_credential_material(text, path, failures)
        if path.name not in {"LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"}:
            audit_public_identity(text, path, failures)

        if path.suffix.lower() == ".cs" and path.name != "AevrixSecureTransport.cs" and DIRECT_HTTP.search(text):
            failures.append(f"direct HttpClient construction outside AevrixSecureTransport: {rel(path)}")

        if path.suffix.lower() == ".py":
            try:
                ast.parse(text, filename=str(path))
            except SyntaxError as exc:
                failures.append(f"python syntax error {rel(path)}:{exc.lineno}: {exc.msg}")

    if not (ROOT / "LICENSE").is_file():
        failures.append("LICENSE missing")
    if not (ROOT / "SECURITY.md").is_file():
        failures.append("SECURITY.md missing")

    audit_patch_queue_policy(failures)
    audit_privacy_root_policy(failures)

    registry_path = ROOT / "docs" / "manifests" / "repository-intelligence.json"
    try:
        registry = json.loads(registry_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        failures.append(f"repository intelligence registry cannot be read: {exc}")
    else:
        failures.extend(f"repository intelligence: {failure}" for failure in audit_registry(registry))

    manifest = []
    for path in sorted(files):
        data = path.read_bytes()
        manifest.append({"path": rel(path), "sha256": hashlib.sha256(data).hexdigest(), "sizeBytes": len(data)})

    report = {
        "schemaVersion": 1,
        "status": "PASS" if not failures else "FAIL",
        "filesAudited": len(files),
        "failures": failures,
        "warnings": warnings,
        "files": manifest,
    }
    output = ROOT / "public-source-audit.json"
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("status", "filesAudited", "failures", "warnings")}, indent=2, ensure_ascii=False))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
