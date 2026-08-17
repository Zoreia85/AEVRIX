# AEVRIX Windows Desktop — Product/UX ownership and readiness

Status: **IN DEVELOPMENT — NOT HOMOLOGATED**  
Owner scope: Windows product surface / UX / desktop application  
Canonical implementation: `apps/aevrix-windows/`

## Scope boundary

This product track owns what the Windows user installs, sees and operates: shell/navigation, first-run onboarding, configuration, Command Center, Mission Control, user-facing logs/activity, permissions UX, local/remote mode presentation, installer experience, update/recovery UX, accessibility and packaging experience.

It does **not** redefine the security/runtime authority implemented by the Windows Runtime/Sandbox/Security track, and it does **not** declare final release readiness. Final homologation belongs to the QA/readiness gates. The desktop must consume those states fail-closed and must never turn absence of evidence into a healthy status.

## Evidence rules

1. Chat claims are not implementation evidence.
2. A UI capability is accepted only when code exists in the repository and the exact candidate builds.
3. Runtime/security states shown to users must originate from a real probe or remain explicitly `not verified`/`blocked`.
4. Session activity is not the canonical Proof Ledger. Product-facing logs must be privacy-safe summaries and may not expose raw payloads, secrets or sensitive evidence.
5. `100%` requires all scorecard domains below to reach their acceptance criteria on the exact release candidate.

## Progress scorecard

The score is intentionally conservative. Each domain is worth 10 points; partial points require repository evidence.

| Domain | Accepted | Candidate | Evidence / remaining gap |
|---|---:|---:|---|
| Shell, navigation and responsive layout | 7/10 | 8/10 | First-run and Settings/Security become real routed surfaces; visual regression/responsive coverage remains. |
| Command Center and verified local state | 7/10 | 7/10 | EngineHost start/restart/stop, authenticated ping and health revocation are surfaced; remote state is not yet integrated. |
| First-run onboarding and initial configuration | 2/10 | 7/10 | Candidate adds persistent first-run profile, structural integrity probe, authenticated EngineHost gate, explicit TPM identity, operating mode, permission posture and fail-closed completion. Remote enrollment/session is still pending. |
| Mission Control | 5/10 | 5/10 | Verified local EngineHost state is mirrored while remote/queue states remain fail-closed; real orchestrator mission feed is pending. |
| Activity and user-facing logs | 5/10 | 5/10 | Bounded privacy-safe operational session journal exists; persistent canonical Proof Ledger integration is pending. |
| Permissions, settings and security UX | 1/10 | 5/10 | Candidate adds a real Settings/Security surface, profile repair, identity/mode/EngineHost/remote state and no security-bypass toggles. OS/policy-backed permission details remain incomplete. |
| Local/remote mode UX | 2/10 | 4/10 | Candidate adds explicit local/remote selection and blocks remote completion without endpoint, certificate and authenticated session. Capability negotiation and live remote session remain pending. |
| Installer lifecycle experience | 7/10 | 7/10 | NSIS package plus interruption recovery, repair, upgrade, downgrade resistance and uninstall test harness exist; polished interactive UX/evidence promotion remains. |
| Update and recovery UX | 3/10 | 3/10 | Installer recovery/repair mechanics exist; in-app update channel, progress, rollback and user recovery surfaces are pending. |
| Accessibility, packaging and release evidence | 4/10 | 4/10 | Automation names and Windows build/readiness workflows exist; accessibility regression suite, signed release evidence and final homologation remain. |

**Accepted desktop/product score: 43/100 (43%).**  
**Candidate score on `feature/desktop-first-run-security`: 55/100 (55%), pending exact Windows CI.**

The candidate delta is **+12 percentage points**. It becomes accepted only after the exact branch candidate passes the required Windows build/tests and is promoted to `main`. This percentage measures only this Desktop/Product/UX track and must not be substituted for the global AEVRIX homologation percentage.

## Accepted increment — Mission Control + Activity

Merged pull request: `#350`  
Canonical merge commit: `b50d9d4190fa44849d276111442a40f630d67cf2`

Implemented and accepted:

- `Mission Control` is a real routed surface rather than a generic placeholder.
- Local EngineHost status is mirrored from the same authenticated state used by Command Center.
- Remote brain and mission queue remain explicitly unverified/unavailable instead of simulated.
- `Activity` is a real routed surface backed by a bounded in-memory operational journal.
- Journal entries are normalized, size-limited, newest-first and intended only for privacy-safe user summaries.
- EngineHost verification, restart, stop, unexpected termination, health-proof loss and unavailable policy validation generate friendly session events.
- Core tests cover journal ordering, retention, normalization and input validation.

## Current candidate — First-run + Settings/Security

Branch: `feature/desktop-first-run-security`

Implemented in the candidate:

- first launch routes to `Inicialização segura` until a valid local completion is persisted;
- first-run state persists only privacy-safe configuration metadata, never access tokens or private-key material;
- corrupt/invalid first-run state fails closed and requires explicit profile recreation;
- structural local integrity probe requires Desktop/Core/EngineHost artifacts, rejects reparse points and computes SHA-256 for diagnostic evidence while explicitly not claiming Authenticode equivalence;
- EngineHost gate consumes the existing authenticated Ping and is revoked when health proof is lost;
- TPM-backed ECDSA P-256 non-exportable device identity can be created/reopened explicitly; software fallback is not automatic;
- local supervised and remote governed modes are explicit choices;
- remote governed completion requires configured HTTPS endpoint, validated device certificate and authenticated remote session; missing proof blocks completion;
- permission posture requires explicit acknowledgement that Desktop cannot elevate privileges, disable isolation or bypass runtime policy;
- Settings/Security is a real surface showing installation id, first-run state, operating mode, local identity state, EngineHost proof and remote state without unsafe bypass toggles;
- Core tests cover local/remote readiness, fail-closed EngineHost loss, profile persistence/corruption and structural integrity hashing.

## CI evidence

PR #350 candidate `b37b2e05e381f848939f9f51ced1d57a61a898f1` passed on `windows-latest`:

- Source Policy: PASS;
- Desktop Release build: PASS;
- Windows Core tests: PASS;
- Remote Security tests: PASS;
- Orchestrator Judge tests: PASS.

The later Desktop evidence workflow was corrected by PR #354 so post-merge build evidence is pinned to the triggering immutable SHA and fails closed on identity mismatch.

The current first-run/security candidate must pass the same Windows CI before its 55% score can become accepted.

## Next autonomous priorities after this candidate

1. Connect device enrollment and authenticated remote session to the first-run flow through the existing secure transport, using signed/bootstrap configuration rather than free-form secret entry.
2. Connect Mission Control to the real orchestrator feed using an explicit read model and bounded event stream.
3. Connect Activity to the canonical Proof Ledger through a privacy-filtered projection while keeping raw evidence out of generic UI logs.
4. Add policy-backed OS permission/capability status to Settings/Security without exposing bypass controls.
5. Add in-app update/recovery surfaces bound to signed update metadata and rollback policy.
6. Add automated accessibility, keyboard/focus and visual regression coverage.

## Update protocol

Every completed Desktop/Product action must update this scorecard only when new repository evidence justifies a point change. Every status report must end with the accepted Desktop/Product percentage, any higher candidate percentage still waiting for evidence, the delta and the next highest-value blocker.
