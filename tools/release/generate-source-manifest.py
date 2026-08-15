#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "docs" / "manifests" / "SOURCE_MANIFEST.json"
EXCLUDED_PARTS = {".git", "bin", "obj", "artifacts", "__pycache__", ".pytest_cache"}
EXCLUDED_NAMES = {"SOURCE_MANIFEST.json", "public-source-audit.json"}

records = []
for path in sorted(ROOT.rglob("*")):
    if not path.is_file():
        continue
    if path.name in EXCLUDED_NAMES or any(part in EXCLUDED_PARTS for part in path.parts):
        continue
    data = path.read_bytes()
    records.append(
        {
            "path": path.relative_to(ROOT).as_posix(),
            "sizeBytes": len(data),
            "sha256": hashlib.sha256(data).hexdigest(),
        }
    )

canonical = json.dumps(records, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
payload = {
    "schemaVersion": 1,
    "algorithm": "SHA-256",
    "fileCount": len(records),
    "recordsSha256": hashlib.sha256(canonical).hexdigest(),
    "files": records,
}
OUTPUT.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"{payload['fileCount']} files; records SHA-256 {payload['recordsSha256']}")
