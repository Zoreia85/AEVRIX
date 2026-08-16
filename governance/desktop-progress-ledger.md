# Windows Desktop / UX progress ledger

Tracker: #290
Scoring rule: canonical points only after the exact promoted candidate has reproducible evidence.

## Current state — 2026-08-16

Canonical score: **0 / 100** under the strict exact-SHA rule.

Why 0 despite code existing in `main`: the current privacy-safe bot root has not yet received a `.NET core` run tied to that exact bot SHA. This is an evidence gap, not a declaration that the tree is broken.

### Verified candidate evidence

- `37ea0c16675e494ab78e890b61bee17ce630f493`: Windows Desktop build PASS; Windows Core PASS; Remote Security PASS; Orchestrator Judge PASS; Source Policy PASS.
- Runtime-aware/first-run candidate derived from the current canonical Desktop content is being revalidated separately.
- Projects catalog is stacked on that candidate and must pass its own exact-head Windows build before it is considered ready for promotion.

### Implemented but not yet canonically credited

- WinUI shell and navigation.
- New Investigation preparation screen, fail-closed without policy engine.
- authenticated EngineHost status binding using `EngineHostSupervisor` + `GetEngineStatus` / `engine_ready`.
- side-by-side EngineHost lifecycle owned by Desktop.
- first-run local TPM identity preparation with no automatic software fallback.
- read-only canonical `ProjectRepository` catalog surface.

### Still incomplete

- exact privacy-root gate evidence/promotion control.
- installer lifecycle.
- installed-package integrity/signature/hash verification.
- remote enrollment/authentication/workspace.
- remote-brain status, mission queue and security posture binding.
- governed New Investigation execution.
- Mission Control, Evidence, Blueprint and Research Browser product surfaces.
- accessibility/scaling/localization/theme verification matrix.
- update/recovery/offline/degraded/performance gates.
