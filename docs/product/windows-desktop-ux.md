# AEVRIX Windows Desktop — Product/UX ownership and readiness

Status: **IN DEVELOPMENT — NOT HOMOLOGATED**  
Owner scope: Windows product surface / UX / desktop application  
Canonical implementation: `apps/aevrix-windows/`

## Scope boundary

This product track owns what the Windows user installs, sees and operates: shell/navigation, first-run onboarding, configuration, Command Center, Mission Control, user-facing logs/activity, permissions UX, local/remote mode presentation, installer experience, update/recovery UX, accessibility and packaging experience.

It does **not** redefine the security/runtime authority implemented by the Windows Runtime/Sandbox/Security track, and it does **not** declare final release readiness. Final homologation belongs to the QA/readiness gates. The desktop must consume those states fail-closed and must never turn absence of evidence into a healthy status.

## Evidence rules

1. Chat claims are not implementation evidence.
2. A UI capability is implemented only when code exists in the repository and the exact candidate builds.
3. Runtime/security states shown to users must originate from a real probe or remain explicitly `not verified`/`blocked`.
4. Session activity is not the canonical Proof Ledger. Product-facing logs must be privacy-safe summaries and may not expose raw payloads, secrets or sensitive evidence.
5. `100%` requires all scorecard domains below to reach their acceptance criteria on the exact release candidate.

## Progress scorecard

The score is intentionally conservative. Each domain is worth 10 points; partial points require repository evidence.

| Domain | Points | Evidence / remaining gap |
|---|---:|---|
| Shell, navigation and responsive layout | 7/10 | WinUI shell and primary navigation exist; several routes remain planned placeholders. |
| Command Center and verified local state | 7/10 | EngineHost start/restart/stop, authenticated ping and health revocation are surfaced; remote state is not yet integrated. |
| First-run onboarding and initial configuration | 2/10 | Authorized-analysis form exists, but there is no complete first-run enrollment/configuration journey. |
| Mission Control | 5/10 | Dedicated surface now mirrors verified local EngineHost state and preserves fail-closed remote/queue states; real orchestrator mission feed is pending. |
| Activity and user-facing logs | 5/10 | Bounded privacy-safe operational session journal exists; persistent canonical Proof Ledger integration is pending. |
| Permissions, settings and security UX | 1/10 | Navigation contract exists; dedicated implemented settings/permission workflows are pending. |
| Local/remote mode UX | 2/10 | Local supervised EngineHost is represented; authenticated remote mode selection/status/recovery is pending. |
| Installer lifecycle experience | 7/10 | NSIS package plus interruption recovery, repair, upgrade, downgrade resistance and uninstall test harness exist; polished interactive UX/evidence promotion remains. |
| Update and recovery UX | 3/10 | Installer recovery/repair mechanics exist; in-app update channel, progress, rollback and user recovery surfaces are pending. |
| Accessibility, packaging and release evidence | 4/10 | Automation names and Windows build/readiness workflows exist; accessibility regression suite, signed release evidence and final homologation remain. |

**Current desktop/product score: 43/100 (43%).**

This percentage measures only this Desktop/Product/UX track. It must not be substituted for the global AEVRIX homologation percentage.

## Current increment — Mission Control + Activity

Branch: `feature/desktop-ux-mission-activity`

Implemented in this increment:

- `Mission Control` is a real routed surface rather than a generic placeholder.
- Local EngineHost status is mirrored from the same authenticated state used by Command Center.
- Remote brain and mission queue remain explicitly unverified/unavailable instead of simulated.
- `Activity` is a real routed surface backed by a bounded in-memory operational journal.
- Journal entries are normalized, size-limited, newest-first and intended only for privacy-safe user summaries.
- EngineHost verification, restart, stop, unexpected termination, health-proof loss and unavailable policy validation generate friendly session events.
- Core tests cover journal ordering, retention, normalization and input validation.

## CI evidence path

Pull requests touching the Desktop/Core are validated by `.github/workflows/dotnet-core.yml` on `windows-latest`, including:

- Desktop Release build;
- Windows Core tests;
- Remote Security tests;
- Orchestrator Judge tests.

After promotion to `main`, `.github/workflows/desktop-build-evidence.yml` records exact Desktop binary SHA-256 evidence, and `windows-readiness-v2.yml` performs the broader Windows candidate probe.

## Next autonomous priorities

1. Complete first-run onboarding: integrity -> device enrollment/authentication -> operating mode -> permissions -> readiness result.
2. Implement Settings/Security UX backed by real policy state, with no security toggle that can bypass authority.
3. Connect Mission Control to the real orchestrator feed using an explicit read model and bounded event stream.
4. Connect Activity to the canonical Proof Ledger through a privacy-filtered projection while keeping raw evidence out of generic UI logs.
5. Implement local/remote mode selection with authenticated capability negotiation and explicit degraded/offline states.
6. Add in-app update/recovery surfaces bound to signed update metadata and rollback policy.
7. Add automated accessibility checks and keyboard/focus regression coverage.

## Update protocol

Every completed Desktop/Product action must update this scorecard only when new repository evidence justifies a point change. Every status report must end with the current Desktop/Product percentage, the delta from the previous score and the next highest-value blocker.
