# AEVRIX — Public Sanitation Status

Authority: `Zoreia85/AEVRIX#494`. `Zoreia85/AEVRIX` PUBLIC is the temporary operational line while private GitHub Actions capacity is unavailable. `AEVRIX-Black-Core` remains PRIVATE/FROZEN.

## Current sanitation snapshot — 2026-08-17 BRT

- Historical public inventory before this sanitation branch: 251 branches including `main`.
- Dedicated sanitation branch created: `sanitation/public-cleanup-ledger-v1`.
- Therefore current physical branch surface is at least 252 until old refs can be deleted safely.
- Open functional PRs after classification: #493 and #479 only; both remain DRAFT/QUARANTINE.
- PR #364 is CLOSED without merge and its branch/provenance is retained as `BLOCKED_UNTIL_PRIVATE / ARCHIVE_EVIDENCE`.
- PR #496 is CLOSED/MERGED after exact-head public CI and S4 evidence. It is integration evidence only; it does not prove final Windows AVA/homologation.

## PR classifications

- #496 — `INTEGRATED / WINDOWS_DIAGNOSTICS_EVIDENCE`; exact candidate `5e218abb773b0c3ce180ed09cc040a1e9f418413`; Source Policy PASS, .NET core PASS, Windows quarantine CI PASS, payload artifact recorded by S4. Final installed first-run/installer AVA remains pending.
- #493 — `QUARANTINE / MERGE_CANDIDATE`; Android vendor-integrity/App Bundle evidence. Must be reconciled onto current `main` because its capability-registry delta is destructive/stale relative to newer registry state. No merge until rebuilt and S4 PASS.
- #479 — `QUARANTINE / MERGE_CANDIDATE`; fail-closed mobile artifact admission. Existing public Source Policy + mobile-lab runs were green on its exact historical head, but current-base reconciliation/security/regression/S4 PASS remain required.
- #364 — `BLOCKED_UNTIL_PRIVATE / ARCHIVE_EVIDENCE`; AI provider budget logic is preserved but no longer advances in public mode.

## Branch sanitation policy

Presumptive classifications, always subject to equivalence/reference scan before physical deletion:

- `tmp/*` => `TRASH_CANDIDATE`.
- stale `validation/*` => `ARCHIVE_EVIDENCE` or `TRASH_CANDIDATE` after exact result/hash retention.
- superseded `diagnostic/*` => `ARCHIVE_EVIDENCE` or `TRASH_CANDIDATE` after outcome capture.
- repeated `*-v1/v2/v3`, `*-clean-*`, `*-replay`, `*-canonical` => retain newest proven survivor; older copies become `TRASH_CANDIDATE` after equivalence/reference scan.
- crown-jewel/private-only work => `BLOCKED_UNTIL_PRIVATE`; never declassify automatically.

## Promotion rule

`main` = `INTEGRATION_APPROVED`, never automatic `RELEASE_HOMOLOGATED`.

Every promotion must bind: owner stream, exact base/head SHA, changed files/contracts, tests actually executed, policy/security result, compatibility/regression result, unresolved FAIL/SKIP/blockers and independent S4 verdict.

Any mandatory FAIL, stale/missing mandatory evidence, mandatory SKIP, unknown provenance, unresolved incompatibility or IP-boundary violation => `QUARANTINE`.

## Main hygiene correction

Earlier documentation-only Contents API writes produced repeated administrative commits on `main`. Do not rewrite history or force-push. No further direct writes to `main` are permitted. This sanitation ledger now evolves only on `sanitation/public-cleanup-ledger-v1` and must enter `main` only through normal PR/gates.

## Physical deletion constraint

The currently exposed connector does not provide delete-ref/branch deletion. Therefore branch purge is not claimed. We may classify and close PRs, preserve evidence, and prepare a deletion ledger; actual ref deletion requires deletion-capable GitHub access. Until then, physical branch count remains higher than the intended final operational surface.
