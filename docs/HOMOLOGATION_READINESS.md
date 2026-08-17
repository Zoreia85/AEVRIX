# AEVRIX Homologation Readiness V2

This is the canonical QA control plane for the question: **is the Windows AEVRIX candidate ready for use?**

It does not grant runtime capability. It consumes reproducible evidence from implementation streams and issues the release decision.

## Current state

- Target: **Windows**.
- Global readiness: **60%**.
- Release decision: **NOT_HOMOLOGATED / NOT_READY_FOR_GENERAL_USE**.
- Canonical blocker tracker: **#197**.
- Product/E2E handoff: **#289**.
- Filesystem hostile-proof handoffs: **#284 / #288**.
- Installer handoff: **#315**.
- PostgreSQL Authority handoff: **#316**.
- Distribution-security handoff: **#317**.
- Full-product stability handoff: **#318**.
- UX/accessibility handoff: **#319**.
- Mandatory specification: `docs/VALIDATION.md`.
- Machine-readable score: `docs/qa/readiness-model.json`.
- Exact-candidate evaluator: `tools/release/ava-readiness.py`.
- Windows probe: `.github/workflows/windows-readiness-v2.yml`.

## Latest strong exact-candidate evidence

Candidate `3b9193efbac8a4a2daedb88d7eb5368ee647a3b5` was exercised by Windows Readiness V2 run **32030353362** and Windows Installer Candidate run **32030353192**.

The Windows readiness probe reported **60.0% exact-candidate PASS coverage** and remained `NOT_HOMOLOGATED`.

Readiness evidence SHA-256: `49323f043757ccd34d1f8a9c0508f6736f2c86f5382a76be851cd8d8864a1e84`.

Readiness AVA artifact: **9288742018**, digest `sha256:4cc43a11da36b119846f4166367e731d3f29599bd0e890afa32aeaaf3271bfb2`.

Exact automatic PASS gates included:

- EngineHost Release build;
- Desktop Release build;
- Desktop startup/liveness/cleanup smoke bound to the exact Desktop artifact;
- Core regression suite: **105/105 PASS, 0 skipped**;
- Remote Security suite: **6/6 PASS, 0 skipped**;
- Orchestrator/Judge suite: **344/344 PASS, 0 skipped**;
- authenticated EngineHost soak: **250/250 iterations**, five process lifetimes, no failures;
- forced EngineHost crash -> fail-closed post-crash authority -> authenticated restart -> cleanup;
- physical Desktop/WinUI product surface.

The same exact candidate was packaged and exercised by Windows Installer Candidate run **32030353192** on Windows Server 2025 using .NET SDK 10.0.400 and pinned NSIS 3.12. The lifecycle successfully exercised interrupted installation, recovery with the same installer, repair after controlled payload loss, `0.0.1 -> 0.0.2` upgrade, downgrade rejection, uninstall, product-owned residue cleanup and user-data preservation.

Installer hashes:

- `0.0.1`: `2269fc7707bcb29a651d6a863ac6f9ac48f78de789a10f35c6bd39d0e08d538a`;
- `0.0.2`: `fff02088ad8bb0b7d72b679f45f60283f85199839789cba3e0f02360b500c46f`.

Installer candidate evidence SHA-256: `e4bcf66ed23cab0d18b1e1af5c9590be4d92ad370a507c20056e414a2f03b206`.

Installer AVA artifact: **9288900859**, digest `sha256:0cfeaee3ee54fadee96a03e6a662c39cb1333d2187facbf574c2359180e28c96`.

Microsoft Defender scanned **both exact installer executables** in that run. The scan completed with no path-bound detections and the installer SHA-256 values were unchanged after scanning. This is credited as a partial distribution-security result. The AEVRIX installers remain explicitly **unsigned**, so Authenticode and signed update controls remain blockers.

## Two percentages, deliberately separated

### Global readiness

`readinessPercent` measures how much of the release/homologation program has technically matured with credible evidence. It can retain proven engineering progress from earlier exact candidates, but that evidence does **not** automatically validate a different release candidate.

Current value: **60%**.

### Exact-candidate PASS coverage

Every candidate probe calculates what percentage of its exact probe gates are `PASS` for the commit actually checked out and tested. A new commit must earn its own release evidence.

Latest strong Windows readiness value: **60.0%** on candidate `3b9193efbac8a4a2daedb88d7eb5368ee647a3b5`.

The installer lifecycle and Defender evidence are produced by the dedicated exact-candidate installer workflow and credited in the global weighted model only after exact hash binding. Mandatory versioned terms acceptance remains outstanding until independently proven on a releasable candidate.

## Fail-closed rules

1. A successful QA workflow means the **measurement ran successfully**. It does not mean AEVRIX is homologated.
2. `HOMOLOGATED` requires global readiness = 100 **and** every applicable mandatory gate `PASS` for the exact released hashes.
3. `FAIL`, `PARCIAL`, `BLOQUEADO`, `NOT_RUN`, or `INFRASTRUCTURE_INCONCLUSIVE` on a mandatory gate prevents homologation.
4. Missing, skipped, stale or synthetic evidence never counts as PASS.
5. Evidence from a different source commit cannot satisfy exact-candidate gates.
6. Rebuilding after final validation invalidates artifact binding and requires a new AVA run.
7. A workflow/run disappearing after repository-history rewrites cannot remain the only proof source; durable summaries and artifact digests are required.
8. External/manual evidence cannot override automatic build/test gates.
9. Validation is pinned to the triggering exact candidate, not to a moving `main`.
10. QA does not implement a missing product/runtime capability and then self-certify it.

## Current weighted readiness

| Gate | Weight | Points | State |
|---|---:|---:|---|
| Canonical build + CI baseline | 15 | 13 | PARCIAL |
| Windows secure runtime primitives | 20 | 17 | PARCIAL |
| End-to-end Windows product runtime | 15 | 8 | BLOQUEADO |
| Installer + lifecycle | 10 | 8 | PARCIAL |
| Distribution security | 10 | 2 | PARCIAL |
| Execution Authority + PostgreSQL | 10 | 4 | PARCIAL |
| Regression + performance + stability | 10 | 5 | PARCIAL |
| Minimum UX + accessibility | 5 | 1 | NOT_RUN |
| Exact-hash AVA evidence package | 5 | 2 | PARCIAL |
| **Total** | **100** | **60** | **NOT_HOMOLOGATED** |

## Why the score moved from 50% to 60%

- **+8 points — installer lifecycle maturity:** exact-candidate Windows evidence proves interrupted-install recovery, repair after controlled product payload loss, upgrade, downgrade rejection, uninstall, no product-owned residue and user-data preservation.
- **+2 points — exact-artifact Defender evidence:** both exact installer executables were scanned successfully with no path-bound detections and unchanged hashes.
- Installer remains **PARCIAL**, not PASS, because mandatory versioned terms acceptance has not yet been independently proven for the releasable candidate.
- Distribution security remains **PARCIAL**, not PASS, because the exact AEVRIX installers are unsigned and signed update/tamper controls remain outstanding.
- No other gate was promoted by inference.

## Confirmed remaining blockers

### Full Windows E2E / versioned terms acceptance — #289

Secure first-run/profile work exists in the Product/UX stream, but mandatory versioned terms acceptance still requires independent exact-candidate evidence. The complete Desktop -> EngineHost -> worker -> embedded Python -> private Chromium / Research Browser path remains unproven end-to-end.

### Restrictive filesystem isolation — #284 / #288

The runtime fails closed when restrictive filesystem authority is not fully proven. Complete native hostile read/write execution evidence remains required.

### Installer lifecycle remainder — #315

The physical lifecycle is proven on an exact canonical NSIS candidate. Remaining acceptance is the mandatory versioned terms gate on the exact releasable install. `docs/VALIDATION.md` accepts the explicitly approved Windows package format without waiving any lifecycle requirement.

### PostgreSQL Execution Authority — #316

The concrete PostgreSQL-backed implementation and real database AVA remain required.

### Distribution security — #317

Defender exact-artifact evidence is now proven. Authenticode, signed-update manifest, tamper resistance and update rollback/downgrade evidence remain outstanding.

### Full-product stability — #318

EngineHost soak and crash recovery are proven subgates. Full Desktop-to-downstream endurance/recovery/resource testing remains required.

### UX/accessibility — #319

Real Windows visual, DPI, keyboard/navigation and minimum accessibility smoke remains required.

## Current priority order

1. Independently validate mandatory versioned terms acceptance as soon as the queued Stage 9 acceptance implementation is promoted, then continue the full downstream E2E path (#289/#315).
2. Close restrictive filesystem read/write isolation with native hostile Windows proof (#284/#288).
3. Materialize and validate PostgreSQL-backed Execution Authority (#316).
4. Add Authenticode plus signed update/tamper/rollback evidence to the exact distributed artifacts (#317).
5. Execute full-product endurance/resource/recovery campaign (#318).
6. Execute real Windows high-DPI/keyboard/accessibility/visual smoke (#319).
7. Freeze final hashes and assemble the complete release evidence package.

## Responsibility boundary

This QA stream may create probes, validators, evidence formats, regression gates, score models, release checks and blocker issues. Missing capability is routed to its implementation stream and comes back to QA as an exact commit/artifact for independent validation.
