# AEVRIX Homologation Readiness V2

This is the canonical QA control plane for the question: **is the Windows AEVRIX candidate ready for use?**

It does not grant runtime capability. It consumes reproducible evidence from implementation streams and issues the release decision.

## Current state

- Target: **Windows**.
- Global readiness: **50%**.
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

Candidate `1537688b0221b22d1afc9a4121540eed703d7392` was exercised by Windows readiness run **31956694056**.

The exact-candidate probe reported **60.0% PASS coverage** and remained `NOT_HOMOLOGATED`.

Evidence SHA-256: `34b2dc2de752604d0f0d9f5de5f7d25a9022327065b7f94e2cca59f91a2f6cfb`.

The complete AVA package is retained as Actions artifact **9266168521**, digest `sha256:de7f3a5cca8fa23c4d67ea0450472194e039dade06dc4dc74e09d43bd40c12fb`.

Exact automatic PASS gates included:

- EngineHost Release build;
- Desktop Release build with a physical `AEVRIX.Desktop.exe`;
- Desktop startup/liveness/cleanup smoke bound to the exact Desktop artifact SHA-256;
- Core regression suite;
- Remote Security suite;
- Orchestrator/Judge suite;
- authenticated EngineHost soak;
- forced EngineHost crash -> fail-closed post-crash authority -> authenticated restart -> cleanup;
- physical Desktop/WinUI product surface.

The candidate remains blocked from homologation because these automatic gates do **not** prove mandatory first-run/terms, the downstream worker/Python/Chromium path, MSI lifecycle, distribution signing/security, PostgreSQL Authority, full-product endurance or real Windows UX/accessibility.

## Two percentages, deliberately separated

### Global readiness

`readinessPercent` measures how much of the release/homologation program has technically matured with credible evidence. It can retain proven engineering progress from earlier exact candidates, but that evidence does **not** automatically validate a different release candidate.

Current value: **50%**.

### Exact-candidate PASS coverage

Every candidate probe calculates what percentage of its exact mandatory probe gates are `PASS` for the commit actually checked out and tested. A new commit must earn its own release evidence.

Latest proven value: **60.0%** on candidate `1537688b0221b22d1afc9a4121540eed703d7392`.

## Fail-closed rules

1. A successful QA workflow means the **measurement ran successfully**. It does not mean AEVRIX is homologated.
2. `HOMOLOGATED` requires global readiness = 100 **and** every applicable mandatory gate `PASS` for the exact released hashes.
3. `FAIL`, `PARCIAL`, `BLOQUEADO`, `NOT_RUN`, or `INFRASTRUCTURE_INCONCLUSIVE` on a mandatory gate prevents homologation.
4. Missing, skipped, stale or synthetic evidence never counts as PASS.
5. Evidence from a different source commit cannot satisfy exact-candidate gates.
6. Rebuilding after final validation invalidates artifact binding and requires a new AVA run.
7. A workflow/run disappearing after repository-history rewrites cannot remain the only proof source; durable summaries and artifact digests are required.
8. External/manual evidence cannot override automatic build/test gates.
9. `workflow_run` validation is pinned to the triggering `head_sha`, not to a moving `main`.
10. QA does not implement a missing product/runtime capability and then self-certify it; missing implementation is handed back to its owning stream.

## Current weighted readiness

| Gate | Weight | Points | State |
|---|---:|---:|---|
| Canonical build + CI baseline | 15 | 13 | PARCIAL |
| Windows secure runtime primitives | 20 | 17 | PARCIAL |
| End-to-end Windows product runtime | 15 | 8 | BLOQUEADO |
| Installer + lifecycle | 10 | 0 | NOT_RUN |
| Distribution security | 10 | 0 | NOT_RUN |
| Execution Authority + PostgreSQL | 10 | 4 | PARCIAL |
| Regression + performance + stability | 10 | 5 | PARCIAL |
| Minimum UX + accessibility | 5 | 1 | NOT_RUN |
| Exact-hash AVA evidence package | 5 | 2 | PARCIAL |
| **Total** | **100** | **50** | **NOT_HOMOLOGATED** |

## Why the score moved from 49% to 50%

- **+1 point — stability/recovery maturity:** on an exact Windows candidate, QA started the canonical EngineHost through `EngineHostSupervisor`, proved an authenticated ping, killed the real process tree, required post-crash calls to fail closed, restarted under a distinct process identity, proved a second authenticated ping and verified final cleanup. The recovery report was bound to the exact EngineHost SHA-256 produced in the same run.
- `performance-stability` remains **PARCIAL** because local EngineHost recovery is not equivalent to full Desktop -> worker -> Python -> Chromium/product endurance and recovery.

No other gate was promoted by inference.

## Confirmed implementation blockers

### Full Windows E2E — #289

The canonical EngineHost currently promotes Ping, Status, non-repair Diagnostics and GenerateBlueprint. The downstream worker -> embedded Python -> private Chromium / Research Browser runtime path is not physically promoted through the EngineHost boundary yet. Mandatory first-run/terms is also absent from the current launch path.

### Restrictive filesystem isolation — #284 / #288

The runtime distinguishes write-boundary and read-isolation attestations and fails closed when restrictive filesystem authority is not fully proven. The hostile-proof matrix validates evidence completeness, but complete native hostile read/write execution evidence is still required before this block can move above 17/20.

### Installer lifecycle — #315

No canonical MSI/bootstrapper packaging surface has yet returned complete exact-candidate clean-install/repair/upgrade/interruption/uninstall/residue evidence.

### PostgreSQL Execution Authority — #316

The existing orchestration contract (`IExecutionPromotionAuthority`) is present and fail-closed, but the concrete PostgreSQL-backed implementation required for the database AVA is not yet physically present in the canonical tree.

### Distribution security — #317

Exact-artifact Defender, Authenticode, signed-update manifest, tamper and rollback/downgrade evidence remain outstanding.

### Full-product stability — #318

Authenticated EngineHost soak, Desktop startup/cleanup and forced-crash recovery are proven subgates. Full Desktop-to-downstream endurance/recovery/resource testing remains required.

### UX/accessibility — #319

Real Windows visual, DPI, keyboard/navigation and minimum accessibility smoke remains required.

## Current priority order

1. Materialize/prove the downstream worker -> embedded Python -> private Chromium / Research Browser path and first-run/terms (#289).
2. Close restrictive filesystem read/write isolation with native hostile Windows proof (#284, #288).
3. Materialize MSI/bootstrapper and execute clean-install/repair/upgrade/interruption/uninstall/residue AVA (#315).
4. Materialize and validate the PostgreSQL-backed Execution Authority implementation (#316).
5. Bind Defender, Authenticode, signed update and rollback/downgrade evidence to exact distributed artifacts (#317).
6. Execute full-product endurance/resource/recovery campaign (#318).
7. Execute real Windows high-DPI/keyboard/accessibility/visual smoke (#319).
8. Freeze final hashes and assemble the complete release evidence package.

## Responsibility boundary

This QA stream may create probes, validators, evidence formats, regression gates, score models, release checks and blocker issues. Missing capability is routed to its implementation stream and comes back to QA as an exact commit/artifact for independent validation.
