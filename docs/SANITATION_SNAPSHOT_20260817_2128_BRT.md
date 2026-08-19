# AEVRIX sanitation snapshot — 2026-08-17 21:28 BRT

Physical public branch inventory refreshed across all pages: **254 refs/branches**.

Open PR surface at this snapshot:
- #497 — DRAFT / KEEP_ACTIVE / QUARANTINE — Windows installed startup evidence remediation.
- #499 — DRAFT / SANITATION_REVIEW — governance and historical-branch cleanup evidence.

Stale functional PRs already closed without merge and with source branches preserved:
- #364 — BLOCKED_UNTIL_PRIVATE / ARCHIVE_EVIDENCE.
- #493 — ARCHIVE_EVIDENCE / QUARANTINE_SOURCE.
- #479 — ARCHIVE_EVIDENCE / QUARANTINE_SOURCE.

Canonical main observed in the sanitation review remains `c00a80fe35e55c02cd022c614a4645d804016966` until a later canonicalization is explicitly re-read. Privacy-safe rewrite governance #498 requires tree/content-bound evidence rather than merge-SHA ancestry.

First historical cleanup batch contains 17 refs: 1 `tmp/*`, 9 `validation/*`, 7 `diagnostic/*`. Several validation refs now have exact historical PR evidence proving they were validation-only/superseded; physical deletion is not claimed because the connected GitHub interface exposes no delete-ref operation.

Current #497 exact head at this snapshot: `ce007d4525b98f44137ca29a28da21b37bb3d7f7`. Exact-head Source Policy and installed-startup contract have PASS evidence; Windows quarantine CI is still pending/in-progress at this snapshot. No promotion or Canary claim is authorized until the final mandatory gate and S4 review pass.
