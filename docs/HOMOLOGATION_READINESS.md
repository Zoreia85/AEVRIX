# AEVRIX Homologation Readiness V2

This is the canonical QA control plane for the question: **is the Windows AEVRIX candidate ready for use?**

It does not grant runtime capability. It consumes reproducible evidence from implementation streams and issues the release decision.

## Current state

- Target: **Windows**.
- Global readiness: **48%**.
- Release decision: **NOT_HOMOLOGATED / NOT_READY_FOR_GENERAL_USE**.
- Canonical blocker tracker: **#197**.
- Product/E2E handoff: **#289**.
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

Workflow run **31955371631** validated source commit `2611920a3d289a25c079a25fdd52c33d97d6ed09` on real hosted Windows.

The exact-candidate probe reported **53.85% PASS coverage** and remained `NOT_HOMOLOGATED`.

PASS evidence included:

- EngineHost Release build;
- Desktop Release build with a physical `AEVRIX.Desktop.exe`;
- Core regression suite;
- Remote Security suite;
- Orchestrator/Judge suite;
- authenticated EngineHost soak;
- physical Desktop/WinUI product surface.

The complete AVA package is retained as GitHub Actions artifact **9265832940**, digest `sha256:1a9ab6108da1fa63616fd8c84963304fee9a0cf592d9b8a979e76412635f9239`.

This evidence increases engineering/homologation maturity, but does not prove mandatory first-run/terms, complete Desktop-to-downstream E2E, MSI lifecycle, distribution signing/security, real PostgreSQL integration, full-product stability, or real Windows UX/accessibility.

## Two percentages, deliberately separated

### Global readiness

`readinessPercent` measures how much of the release/homologation program has technically matured with credible evidence. It may reuse evidence from previous exact candidates when measuring engineering progress, but that evidence does **not** automatically validate a new release candidate.

Current value: **48%**.

### Exact-candidate PASS coverage

Every candidate probe calculates what percentage of its exact mandatory probe gates are `PASS` for the commit actually checked out and tested. A new commit starts from its own evidence. Old CI cannot silently convert an untested new SHA into PASS.

Latest proven value: **53.85%** on candidate `2611920a3d289a25c079a25fdd52c33d97d6ed09`.

## Fail-closed rules

1. A successful QA workflow means the **measurement ran successfully**. It does not mean AEVRIX is homologated.
2. `HOMOLOGATED` requires global readiness = 100 **and** every applicable mandatory gate `PASS` for the exact released hashes.
3. `FAIL`, `PARCIAL`, `BLOQUEADO`, `NOT_RUN`, or `INFRASTRUCTURE_INCONCLUSIVE` on a mandatory gate prevents homologation.
4. Missing evidence never counts as PASS.
5. Evidence from a different source commit cannot satisfy exact-candidate gates.
6. Rebuilding after final validation invalidates the artifact binding and requires a new AVA run.
7. A workflow/run disappearing after repository history rewrites cannot remain the only proof source; durable summaries and artifact digests are required.
8. External/manual evidence cannot override the automatic build/test gates.

## Durable evidence strategy

The Windows readiness workflow creates a normalized evidence record containing:

- checked-out source commit;
- workflow run identifier;
- Windows runner/image and architecture;
- pinned .NET SDK version;
- EngineHost and Desktop Release file hashes;
- TRX hashes and counts for Core, Remote Security, and Orchestrator/Judge;
- explicit detection of skipped/not-executed tests;
- authenticated EngineHost soak metrics and exact EngineHost hash binding;
- Desktop/WinUI physical-surface preflight;
- exact-candidate gate states;
- evidence SHA-256;
- AVA artifact identifier and artifact digest;
- final release decision.

On trusted runs, the summary is persisted into issue **#197**, and the complete evidence directory is uploaded as a retained Actions artifact.

## Current weighted readiness

| Gate | Weight | Points | State |
|---|---:|---:|---|
| Canonical build + CI baseline | 15 | 13 | PARCIAL |
| Windows secure runtime primitives | 20 | 17 | PARCIAL |
| End-to-end Windows product runtime | 15 | 7 | BLOQUEADO |
| Installer + lifecycle | 10 | 0 | NOT_RUN |
| Distribution security | 10 | 0 | NOT_RUN |
| Execution Authority + PostgreSQL | 10 | 4 | PARCIAL |
| Regression + performance + stability | 10 | 4 | PARCIAL |
| Minimum UX + accessibility | 5 | 1 | NOT_RUN |
| Exact-hash AVA evidence package | 5 | 2 | PARCIAL |
| **Total** | **100** | **48** | **NOT_HOMOLOGATED** |

## Why the score moved from 45% to 48%

- **+2 points — Windows E2E maturity:** the native Desktop/WinUI project now exists and its exact Release candidate builds successfully on Windows. The gate remains `BLOQUEADO` because first-run/terms and the complete Desktop -> EngineHost -> worker -> embedded Python -> private Chromium chain are not yet proven.
- **+1 point — exact-hash evidence:** the complete candidate evidence directory is now retained as a digest-bound AVA artifact. The gate remains `PARCIAL` because mandatory manual/product evidence is incomplete.

No other gate was promoted by inference.

## Current priority order

1. Prove Desktop startup and then the complete Desktop -> EngineHost -> worker -> embedded Python -> private Chromium path (#289).
2. Implement/prove mandatory first-run/terms behavior (#289).
3. Close restrictive filesystem read/write isolation with hostile Windows proof (#284, #288).
4. Materialize MSI/bootstrapper and execute clean-install/repair/upgrade/interruption/uninstall/residue AVA (#315).
5. Execute PostgreSQL-backed Execution Authority integration with zero mandatory skips (#316).
6. Bind Defender, Authenticode, signed update and rollback/downgrade evidence to exact distributed artifacts (#317).
7. Execute full-product endurance/resource/recovery campaign (#318).
8. Execute real Windows high-DPI/keyboard/accessibility/visual smoke (#319).
9. Freeze final hashes and assemble the complete release evidence package.

## Responsibility boundary

This QA stream may create probes, validators, evidence formats, regression gates, score models, release checks and blocker issues. It must not silently implement a missing runtime/product capability and then self-certify it. Missing capability is routed to its implementation stream and comes back to QA as an exact commit/artifact for independent validation.
