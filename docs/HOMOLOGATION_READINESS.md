# AEVRIX Homologation Readiness V2

This is the canonical QA control plane for the question: **is the Windows AEVRIX candidate ready for use?**

It does not grant runtime capability. It consumes reproducible evidence from implementation streams and issues the release decision.

## Current state

- Target: **Windows**.
- Global readiness: **58%**.
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

## Latest exact-candidate evidence

Candidate `f53eaeb8388bb34aad45ebb2a4ef37598bdb76cb` was exercised by Windows readiness run **32024980229** and Windows Installer Candidate run **32024980230**.

The Windows readiness probe reported **60.0% exact-candidate PASS coverage** and remained `NOT_HOMOLOGATED`.

Readiness evidence SHA-256: `f72c9fcba9e2cf60bd6c30a8e55665e715607afa6e700de038d8d5ea921becbd`.

Readiness AVA artifact: **9286850521**, digest `sha256:ab20dd8906d6149229ca6f11c2498cbe27ff137eba23ef51c5bc203cd2e8a573`.

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

The exact installer candidate was exercised by run **32024980230** on Windows Server 2025 using .NET SDK 10.0.400 and pinned NSIS 3.12. The lifecycle successfully exercised interrupted installation, recovery with the same installer, repair after controlled payload loss, `0.0.1 -> 0.0.2` upgrade, downgrade rejection, uninstall, product-owned residue cleanup and user-data preservation.

Installer hashes:

- `0.0.1`: `08aa430cf6a0ecd124ac27f442b913d8deb1cc7fd360c0e3296dd1185a0c5e01`;
- `0.0.2`: `f3d304db82e1e352cf34448621be5d974d28e4f1137835eddc7d08451cb54e67`.

Installer candidate evidence SHA-256: `128170a99961aa59eff325843c1cf7e6cb2f7ea15f10123403dbfcf5218c7c97`.

Installer AVA artifact: **9286912675**, digest `sha256:0b65bb8225ecf96859027d8b9f649932bd35c681190f6659a26ac94d46fad585`.

The exact installer candidates are explicitly **unsigned**. That is a distribution-security blocker, not an installer lifecycle failure.

## Two percentages, deliberately separated

### Global readiness

`readinessPercent` measures how much of the release/homologation program has technically matured with credible evidence. It can retain proven engineering progress from earlier exact candidates, but that evidence does **not** automatically validate a different release candidate.

Current value: **58%**.

### Exact-candidate PASS coverage

Every candidate probe calculates what percentage of its exact probe gates are `PASS` for the commit actually checked out and tested. A new commit must earn its own release evidence.

Latest Windows readiness value: **60.0%** on candidate `f53eaeb8388bb34aad45ebb2a4ef37598bdb76cb`.

The installer lifecycle is tracked separately because it is produced by a dedicated exact-candidate workflow. Its newly proven lifecycle evidence is credited in the global weighted readiness model while mandatory first-run/terms remains outstanding.

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
| Distribution security | 10 | 0 | NOT_RUN |
| Execution Authority + PostgreSQL | 10 | 4 | PARCIAL |
| Regression + performance + stability | 10 | 5 | PARCIAL |
| Minimum UX + accessibility | 5 | 1 | NOT_RUN |
| Exact-hash AVA evidence package | 5 | 2 | PARCIAL |
| **Total** | **100** | **58** | **NOT_HOMOLOGATED** |

## Why the score moved from 50% to 58%

- **+8 points — installer lifecycle maturity:** exact-candidate Windows evidence now proves interrupted installation recovery, repair after controlled product payload loss, upgrade, downgrade rejection, uninstall, no product-owned residue and user-data preservation.
- The installer gate remains **PARCIAL**, not PASS, because mandatory terms/first-run behavior has not been proven by the lifecycle run.
- No distribution-security credit was granted: the exact installers are explicitly unsigned and Defender/AuthentiCode/signed update evidence remains outstanding.
- No other gate was promoted by inference.

## Confirmed remaining blockers

### Full Windows E2E / first-run — #289

Mandatory terms/first-run and the complete Desktop -> EngineHost -> worker -> embedded Python -> private Chromium / Research Browser path remain unproven end-to-end.

### Restrictive filesystem isolation — #284 / #288

The runtime fails closed when restrictive filesystem authority is not fully proven. Complete native hostile read/write execution evidence remains required.

### Installer lifecycle remainder — #315

The physical lifecycle is now proven on an exact canonical NSIS candidate. The remaining installer-side acceptance item is exact-candidate mandatory terms/first-run behavior. The AVA specification now accepts the explicitly approved Windows package format rather than hard-coding MSI, without waiving any lifecycle test.

### PostgreSQL Execution Authority — #316

The concrete PostgreSQL-backed implementation and real database AVA remain required.

### Distribution security — #317

Exact-artifact Defender, Authenticode, signed-update manifest, tamper and rollback/downgrade evidence remain outstanding.

### Full-product stability — #318

EngineHost soak and crash recovery are proven subgates. Full Desktop-to-downstream endurance/recovery/resource testing remains required.

### UX/accessibility — #319

Real Windows visual, DPI, keyboard/navigation and minimum accessibility smoke remains required.

## Current priority order

1. Prove mandatory terms/first-run on the exact installed candidate and continue the full downstream E2E path (#289/#315).
2. Close restrictive filesystem read/write isolation with native hostile Windows proof (#284/#288).
3. Materialize and validate PostgreSQL-backed Execution Authority (#316).
4. Bind Defender, Authenticode, signed update and rollback/tamper evidence to exact distributed artifacts (#317).
5. Execute full-product endurance/resource/recovery campaign (#318).
6. Execute real Windows high-DPI/keyboard/accessibility/visual smoke (#319).
7. Freeze final hashes and assemble the complete release evidence package.

## Responsibility boundary

This QA stream may create probes, validators, evidence formats, regression gates, score models, release checks and blocker issues. Missing capability is routed to its implementation stream and comes back to QA as an exact commit/artifact for independent validation.
