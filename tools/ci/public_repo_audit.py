#!/usr/bin/env python3
from __future__ import annotations

import ast
import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TEXT_SUFFIXES = {".cs", ".py", ".ps1", ".json", ".md", ".yml", ".yaml", ".xml", ".csproj", ".props", ".wxs"}
IGNORED_PARTS = {".git", "bin", "obj", "artifacts", "__pycache__", ".pytest_cache"}

FORBIDDEN_BRANDING = re.compile("(?i)(" + "grupo" + "[ -]?" + "temper|" + "temper" + "researchstudio|" + "temper" + " research studio)")
PRIVATE_KEY = re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")
LIKELY_SECRET = re.compile(r"(?im)^\s*(?:(?:const|static|readonly|var|string)\s+)*(?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|password)\s*=\s*[\"'][^\"']{12,}[\"']")
JSON_SECRET = re.compile(r"(?im)^\s*[\"](?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|password)[\"]\s*:\s*[\"][^\"]{12,}[\"]")
DIRECT_HTTP = re.compile(r"\bnew\s+HttpClient\s*\(")


def source_files():
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.name == "public-source-audit.json" or any(part in IGNORED_PARTS for part in path.parts):
            continue
        if path.suffix.lower() in TEXT_SUFFIXES or path.name in {"LICENSE", "NOTICE", ".gitignore", ".editorconfig"}:
            yield path


def rel(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def main() -> int:
    failures: list[str] = []
    warnings: list[str] = []
    files = list(source_files())

    for path in files:
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
