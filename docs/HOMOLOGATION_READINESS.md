# AEVRIX Homologation Readiness V2

This is the canonical QA control plane for the question: **is the Windows AEVRIX candidate ready for use?**

It does not grant runtime capability. It consumes reproducible evidence from implementation streams and issues the release decision.

## Current state

- Target: **Windows**.
- Global readiness: **45%**.
- Release decision: **NOT_HOMOLOGATED / NOT_READY_FOR_GENERAL_USE**.
- Canonical blocker tracker: **#197**.
- Product-surface blocker: **#289**.
- Mandatory specification: `docs/VALIDATION.md`.
- Machine-readable score: `docs/qa/readiness-model.json`.
- Exact-candidate evaluator: `tools/release/ava-readiness.py`.
- Windows probe: `.github/workflows/windows-readiness-v2.yml`.

## Two percentages, deliberately separated

### Global readiness

`readinessPercent` measures how much of the release/homologation program has technically matured with credible evidence. It may reuse evidence from previous exact candidates when measuring engineering progress, but that evidence does **not** automatically validate a new release candidate.

Current value: **45%**.

### Exact-candidate PASS coverage

Every candidate probe calculates what percentage of its exact mandatory probe gates are `PASS` for the commit actually checked out and tested. A new commit starts from its own evidence. Old CI cannot silently convert an untested new SHA into PASS.

A candidate can therefore have, for example, 45% global readiness and lower exact-candidate coverage. That is expected and safer than inheriting stale release evidence.

## Fail-closed rules

1. A successful QA workflow means the **measurement ran successfully**. It does not mean AEVRIX is homologated.
2. `HOMOLOGATED` requires global readiness = 100 **and** every applicable mandatory gate `PASS` for the exact released hashes.
3. `FAIL`, `PARCIAL`, `BLOQUEADO`, `NOT_RUN`, or `INFRASTRUCTURE_INCONCLUSIVE` on a mandatory gate prevents homologation.
4. Missing evidence never counts as PASS.
5. Evidence from a different source commit cannot satisfy exact-candidate gates.
6. Rebuilding after final validation invalidates the artifact binding and requires a new AVA run.
7. A workflow/run disappearing after repository history rewrites cannot remain the only proof source; durable summaries must be anchored outside ephemeral run history.

## Durable evidence strategy

The V2 Windows readiness workflow creates a normalized evidence record containing:

- checked-out source commit;
- workflow run identifier;
- Windows runner/image and architecture;
- pinned .NET SDK version;
- EngineHost Release artifact file hashes;
- TRX hashes and counts for Core, Remote Security, and Orchestrator/Judge;
- explicit detection of skipped/not-executed tests;
- Desktop/WinUI physical-surface preflight;
- exact-candidate gate states;
- evidence SHA-256;
- final release decision.

On trusted push/manual runs, the summary is persisted into issue **#197**, so the audit trail does not rely exclusively on the lifetime of a GitHub Actions run.

## Manual commands

Validate the global readiness model:

```powershell
python tools/release/ava-readiness.py validate-model --model docs/qa/readiness-model.json
```

The workflow executes the exact-candidate probe automatically. For a final release candidate, use the `Windows readiness V2` manual dispatch with `strict_homologation=true`. The strict run must fail until all mandatory exact-candidate gates are actually PASS.

Platform/device operations that cannot be credibly automated in hosted CI remain manual AVA gates. Their output must be normalized into an external evidence file bound to the exact candidate before strict homologation can pass.

## Current weighted readiness

| Gate | Weight | Points | State |
|---|---:|---:|---|
| Canonical build + CI baseline | 15 | 13 | PARCIAL |
| Windows secure runtime primitives | 20 | 17 | PARCIAL |
| End-to-end Windows product runtime | 15 | 5 | BLOQUEADO |
| Installer + lifecycle | 10 | 0 | NOT_RUN |
| Distribution security | 10 | 0 | NOT_RUN |
| Execution Authority + PostgreSQL | 10 | 4 | PARCIAL |
| Regression + performance + stability | 10 | 4 | PARCIAL |
| Minimum UX + accessibility | 5 | 1 | NOT_RUN |
| Exact-hash AVA evidence package | 5 | 1 | PARCIAL |
| **Total** | **100** | **45** | **NOT_HOMOLOGATED** |

## Current priority order

1. Materialize and test the native Windows Desktop/WinUI product surface (#289).
2. Prove complete hostile filesystem read/write isolation for every restrictive authority actually claimed; otherwise remain fail-closed (#284, #288).
3. Prove Desktop -> EngineHost -> worker -> embedded Python -> private Chromium on one exact candidate.
4. Materialize MSI/bootstrapper and execute clean-install/repair/upgrade/interruption/uninstall/residue AVA.
5. Run PostgreSQL-backed Execution Authority integration with mandatory tests not skipped.
6. Add exact-artifact Defender, Authenticode, signed-update, and rollback/downgrade evidence.
7. Execute stability/soak/resource/recovery gates.
8. Execute high-DPI, keyboard, accessibility, and visual-regression gates.
9. Freeze exact hashes and assemble the final release evidence package.

## Responsibility boundary

This QA stream may create probes, validators, evidence formats, regression gates, score models, release checks and blocker issues. It must not silently implement a missing runtime/product capability and then self-certify it. Missing capability is routed to its implementation stream and comes back to QA as an exact commit/artifact for independent validation.
