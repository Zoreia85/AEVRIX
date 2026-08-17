# AEVRIX Windows Desktop — Product/UX ownership and readiness

Status: **IN DEVELOPMENT — NOT HOMOLOGATED**  
Owner scope: Windows product surface / UX / desktop application  
Canonical implementation: `apps/aevrix-windows/`

## Scope boundary

This product track owns what the Windows user installs, sees and operates: shell/navigation, first-run onboarding, configuration, Command Center, Mission Control, user-facing logs/activity, permissions UX, local/remote mode presentation, installer experience, update/recovery UX, accessibility and packaging experience.

It does **not** redefine the security/runtime authority implemented by the Windows Runtime/Sandbox/Security track, and it does **not** declare final release readiness. Final homologation belongs to the QA/readiness gates. The desktop must consume those states fail-closed and must never turn absence of evidence into a healthy status.

## Evidence rules

1. Chat claims are not implementation evidence.
2. A UI capability is accepted only when code exists in the repository and the exact candidate builds/tests.
3. Runtime/security states shown to users must originate from a real probe or remain explicitly `not verified`/`blocked`.
4. Session activity is not the canonical Proof Ledger. Product-facing logs must be privacy-safe summaries and may not expose raw payloads, secrets or sensitive evidence.
5. `100%` requires all scorecard domains below to reach their acceptance criteria on the exact release candidate.

## Progress scorecard

The score is intentionally conservative. Each domain is worth 10 points; partial points require repository evidence.

| Domain | Accepted points | Evidence / remaining gap |
|---|---:|---|
| Shell, navigation and responsive layout | 8/10 | Command Center, secure first-run, Mission Control, Activity and Settings/Security are real routed WinUI surfaces; visual regression and broader responsive coverage remain. |
| Command Center and verified local state | 7/10 | EngineHost start/restart/stop, authenticated ping and health revocation are surfaced; live remote state is not yet integrated. |
| First-run onboarding and initial configuration | 7/10 | Persistent privacy-safe profile, structural integrity probe, authenticated EngineHost gate, explicit TPM identity, mode, permission posture and fail-closed completion are implemented. Governed remote enrollment/session remains pending. |
| Mission Control | 5/10 | Verified local EngineHost state is mirrored while remote/queue states remain fail-closed; real orchestrator mission feed is pending. |
| Activity and user-facing logs | 5/10 | Bounded privacy-safe operational session journal exists; persistent canonical Proof Ledger projection is pending. |
| Permissions, settings and security UX | 5/10 | Real Settings/Security surface, profile repair, identity/mode/EngineHost/remote state and no security-bypass toggles; OS/policy-backed permission detail remains incomplete. |
| Local/remote mode UX | 4/10 | Explicit local/remote selection exists and remote completion blocks without endpoint, certificate and authenticated session. Capability negotiation and live remote session remain pending. |
| Installer lifecycle experience | 7/10 | NSIS lifecycle harness covers interruption recovery, repair, upgrade, downgrade resistance and uninstall; polished interactive UX and release-distribution gates remain. |
| Update and recovery UX | 3/10 | Installer recovery/repair mechanics exist; in-app signed update channel, progress, rollback and recovery surfaces are pending. |
| Accessibility, packaging and release evidence | 4/10 | Automation names and exact-SHA Windows build/readiness workflows exist; accessibility regression, signed release evidence and final homologation remain. |

**Accepted desktop/product score: 55/100 (55%).**

The accepted delta from the previous score is **+12 percentage points**, promoted only after the exact PR candidate and the post-merge canonical commit passed the required Windows evidence gates. This percentage measures only this Desktop/Product/UX track and must not be substituted for the global AEVRIX homologation percentage.

## Accepted increment — Mission Control + Activity

Merged pull request: `#350`  
Canonical merge commit: `b50d9d4190fa44849d276111442a40f630d67cf2`

Implemented and accepted:

- `Mission Control` is a real routed surface rather than a generic placeholder;
- local EngineHost status is mirrored from the same authenticated state used by Command Center;
- remote brain and mission queue remain explicitly unverified/unavailable instead of simulated;
- `Activity` is backed by a bounded in-memory operational journal;
- journal entries are normalized, size-limited, newest-first and intended only for privacy-safe user summaries;
- EngineHost verification, restart, stop, unexpected termination, health-proof loss and unavailable policy validation generate friendly session events.

## Accepted increment — Secure first-run + Settings/Security

Merged pull request: `#365`  
Tested PR head: `589c63f36bcb805c786deb7c0cbf201a794df1ba`  
Canonical merge commit: `db967d0a6870969e230cc9a96167adc31b422445`

Implemented and accepted:

- first launch routes to `Inicialização segura` until a valid local completion is persisted;
- first-run persists only privacy-safe configuration metadata, never access tokens or private-key material;
- corrupt/invalid persisted state fails closed and requires explicit profile recreation;
- structural local integrity requires Desktop/Core/EngineHost artifacts, rejects reparse points and computes SHA-256 diagnostic evidence without claiming equivalence to Authenticode;
- EngineHost gate consumes the real authenticated Ping and is revoked when health proof is lost;
- TPM-backed ECDSA P-256 non-exportable identity can be created/reopened explicitly, with no automatic downgrade to software fallback;
- local supervised and remote governed modes are explicit choices;
- remote governed completion requires configured HTTPS endpoint, validated device certificate and authenticated remote session; absent proof blocks completion;
- permission posture requires explicit acknowledgement that Desktop cannot elevate privileges, disable isolation or bypass runtime policy;
- Settings/Security exposes installation ID, first-run state, operating mode, local identity, EngineHost proof and remote state, without unsafe bypass toggles;
- XAML initialization handlers are guarded until the first-run profile is loaded, preventing early event dispatch from consuming uninitialized state;
- Core tests cover local/remote readiness, EngineHost proof loss, profile persistence/corruption and structural integrity hashing.

## Exact Windows evidence

PR #365 exact head `589c63f36bcb805c786deb7c0cbf201a794df1ba` passed:

- Source Policy: PASS;
- Desktop Release build: PASS;
- Windows Core tests: PASS;
- Remote Security tests: PASS;
- Orchestrator Judge tests: PASS.

Post-merge canonical commit `db967d0a6870969e230cc9a96167adc31b422445` passed:

- Desktop Build Evidence run `32030470603`: exact-candidate checkout PASS; immutable SHA identity assertion PASS; WinUI Release build PASS; exact binary evidence recording PASS;
- Windows Readiness V2 run `32030470708`: candidate identity PASS; QA evaluator/model validation PASS; EngineHost build PASS; Desktop build PASS; Desktop startup/cleanup smoke PASS; authenticated soak PASS; forced-crash recovery PASS; Windows Core PASS; Remote Security PASS; Orchestrator Judge PASS; fail-closed evidence PASS; AVA package PASS; readiness summary PASS.

The installer-lifecycle import in that particular readiness run was `skipped` because no installer lifecycle artifact existed for that exact Desktop merge SHA. It was not converted into a false PASS and does not raise the Desktop/Product score.

## Next autonomous priorities

1. Connect device enrollment and authenticated remote session to first-run through the existing secure transport, using governed/signed bootstrap configuration rather than free-form secret entry.
2. Connect Mission Control to the real orchestrator feed using an explicit read model and bounded event stream.
3. Connect Activity to the canonical Proof Ledger through a privacy-filtered projection while keeping raw evidence out of generic UI logs.
4. Add policy-backed OS permission/capability status to Settings/Security without exposing bypass controls.
5. Add in-app update/recovery surfaces bound to signed update metadata and rollback policy.
6. Add automated accessibility, keyboard/focus and visual regression coverage.

## Update protocol

Every completed Desktop/Product action must update this scorecard only when new repository evidence justifies a point change. Every status report must end with the accepted Desktop/Product percentage, the delta from the previous accepted score and the next highest-value blocker.
