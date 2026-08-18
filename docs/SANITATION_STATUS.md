# AEVRIX — Public Sanitation Status

Authority: `Zoreia85/AEVRIX#494`. `Zoreia85/AEVRIX` PUBLIC is the temporary operational line while private GitHub Actions capacity is unavailable. `AEVRIX-Black-Core` remains PRIVATE/FROZEN.

## Current sanitation snapshot — 2026-08-17 BRT

- Historical public inventory before this sanitation branch: 251 branches including `main`.
- Dedicated sanitation branch: `sanitation/public-cleanup-ledger-v1`.
- Current branch surface is therefore at least 252 until old refs can be deleted safely.
- Current canonical main observed: `c00a80fe35e55c02cd022c614a4645d804016966`, bot-authored privacy-safe canonical commit.
- Functional stale PR surface was reduced: #364, #493 and #479 are now CLOSED without merge; their branches/provenance remain preserved.
- PR #496 is CLOSED/MERGED as historical review metadata; its approved Windows diagnostic content is physically present in current canonical main.
- The sanitation governance PR #499 is the only intentionally active sanitation PR from this cycle and remains DRAFT/evidence-only.

## Canonical promotion model — issue #498

`main` is rewritten by `.github/workflows/privacy-root-rewrite.yml`. The canonicalizer preserves the resulting tree/content while replacing non-bot commit identity with a bot-authored canonical SHA. Therefore merge-SHA ancestry is not sufficient promotion evidence.

For `INTEGRATION_APPROVED`, bind:
1. reviewed candidate head SHA;
2. candidate/resulting tree identity or exact file/hash manifest;
3. exact tests/policy/security/regression evidence;
4. promotion through the authoritative Bot Patch Queue or another explicitly compatible bot-authored mechanism;
5. resulting canonical bot SHA on `main`;
6. proof of expected tree/content equivalence after canonicalization;
7. post-canonical evidence gate when applicable.

A PR reporting `merged=true` is historical review metadata only. S0/S4 must reconcile the current canonical bot SHA and tree/content after each promotion.

## PR dispositions

- #496 — `CANONICAL_CONTENT_PRESENT / WINDOWS_DIAGNOSTICS_EVIDENCE`; reviewed candidate `5e218abb773b0c3ce180ed09cc040a1e9f418413`; Source Policy PASS, .NET core PASS, Windows quarantine CI PASS, payload artifact recorded by S4. `StartupFailureReporter.cs` and `.github/workflows/windows-quarantine-ci.yml` are physically present on canonical main. Final installed first-run/installer AVA remains pending.
- #493 — CLOSED without merge; `ARCHIVE_EVIDENCE / QUARANTINE_SOURCE`. Android vendor-integrity/App Bundle code remains on `feature/mobile-android-integrity-v7`. Must be reconstructed from current canonical main because its old capability-registry delta would delete newer governance entries.
- #479 — CLOSED without merge; `ARCHIVE_EVIDENCE / QUARANTINE_SOURCE`. Fail-closed admission code remains on `feature/mobile-artifact-admission-gate-v1`. Historical tests remain evidence for old head only; current-base reconstruction and full gates required before future promotion.
- #364 — CLOSED without merge; `BLOCKED_UNTIL_PRIVATE / ARCHIVE_EVIDENCE`. AI provider budget logic remains preserved and does not advance in public mode.
- #499 — OPEN/DRAFT sanitation ledger. Review/evidence only; do not ordinary-merge into canonical main.

## Branch sanitation policy

Presumptive classifications, always subject to equivalence/reference scan before physical deletion:

- `tmp/*` => `TRASH_CANDIDATE`.
- stale `validation/*` => `ARCHIVE_EVIDENCE` or `TRASH_CANDIDATE` after exact result/hash retention.
- superseded `diagnostic/*` => `ARCHIVE_EVIDENCE` or `TRASH_CANDIDATE` after outcome capture.
- repeated `*-v1/v2/v3`, `*-clean-*`, `*-replay`, `*-canonical` => retain newest proven survivor; older copies become `TRASH_CANDIDATE` after equivalence/reference scan.
- crown-jewel/private-only work => `BLOCKED_UNTIL_PRIVATE`; never declassify automatically.

## Promotion rule

`main` = `INTEGRATION_APPROVED`, never automatic `RELEASE_HOMOLOGATED`.

Every candidate must bind owner stream, exact pre-canonical candidate SHA/tree or content manifest, tests actually executed, policy/security result, compatibility/regression result, unresolved FAIL/SKIP/blockers, independent S4 verdict, resulting canonical bot SHA, and post-canonical content/tree equivalence.

Any mandatory FAIL, stale/missing mandatory evidence, mandatory SKIP, unknown provenance, unresolved incompatibility, canonical tree mismatch or IP-boundary violation => `QUARANTINE`.

## Main hygiene correction

Earlier documentation-only Contents API writes produced repeated administrative canonicalizations on `main`. Do not rewrite history or force-push manually. No further direct writes to `main` are permitted. PRs are review/evidence surfaces; approved deltas are promoted using the repository's canonical Bot Patch Queue model, not ordinary merge ancestry assumptions.

## Physical deletion constraint

The currently exposed connector does not provide delete-ref/branch deletion. Therefore branch purge is not claimed. We may classify/close PRs, preserve evidence and prepare a deletion ledger; actual ref deletion requires deletion-capable GitHub access. Until then, physical branch count remains higher than the intended final operational surface.
