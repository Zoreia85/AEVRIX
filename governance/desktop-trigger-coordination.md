# Desktop trigger coordination

Observed: 2026-08-16
Scope: AEVRIX Windows Desktop / UX

The Desktop work is being developed manually in small, testable candidates while the active AEVRIX Master, Architecture, Windows Runtime and Audit automations continue on their own schedules.

Rules for Desktop changes:

1. Reconcile canonical `main` before every promotion attempt.
2. Never force-update a moving branch or overwrite another automation's tree.
3. Keep runtime/security semantics owned by the Core/Runtime contracts; Desktop consumes them fail-closed.
4. Validate exact candidate SHAs through `Source policy` and `.NET core` before promotion.
5. Treat the privacy-safe bot root as canonical only after the exact content has reproducible evidence; do not infer PASS from a predecessor commit merely because the tree was copied.
6. Do not create local or remote success states that the underlying subsystem has not proven.
7. Report Desktop completion separately from global AEVRIX readiness and from final homologation.

Current stacked candidates:

- `candidate/windows-desktop-runtime-aware-v3-20260816`: runtime-aware shell + first-run TPM posture.
- `feature/windows-desktop-projects-v1`: read-only canonical local Projects catalog, stacked on the runtime-aware candidate.

The dedicated Desktop automation is currently paused by the account's active-task limit. The active AEVRIX Master automation still includes Product Windows/UX in its reconciliation scope, so manual Desktop work must continue to obey the coordination rules above until a dedicated slot is available.
