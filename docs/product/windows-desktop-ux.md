# AEVRIX Windows Desktop — Product/UX ownership and readiness

Status: **IN DEVELOPMENT — NOT HOMOLOGATED**  
Owner scope: Windows product surface / UX / desktop application  
Canonical implementation: `apps/aevrix-windows/`

## Scope boundary

This product track owns what the Windows user installs, sees and operates: shell/navigation, first-run onboarding, project surfaces, configuration, Command Center, Mission Control, user-facing logs/activity, permissions/security UX, local/remote mode presentation, installer experience, update/recovery UX, accessibility and packaging experience.

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
| Shell, navigation and responsive layout | 9/10 | Command Center, secure first-run, Projects credential surface, Mission Control, Activity and Settings/Security are real routed WinUI surfaces; visual regression and broader responsive coverage remain. |
| Command Center and verified local state | 7/10 | EngineHost start/restart/stop, authenticated ping and health revocation are surfaced; live remote state is not yet integrated. |
| First-run onboarding and initial configuration | 7/10 | Persistent privacy-safe profile, structural integrity probe, authenticated EngineHost gate, explicit TPM identity, mode, permission posture and fail-closed completion are implemented. Governed remote enrollment/session remains pending. |
| Mission Control | 5/10 | Verified local EngineHost state is mirrored while remote/queue states remain fail-closed; real orchestrator mission feed is pending. |
| Activity and user-facing logs | 5/10 | Bounded privacy-safe operational session journal exists; persistent canonical Proof Ledger projection is pending. |
| Permissions, settings and security UX | 7/10 | Settings/Security plus project-local credential UX now expose guarded identity and access configuration without secret re-display or bypass toggles. OS/policy-backed permission detail, browser E2E credential execution and broader recovery UX remain incomplete. |
| Local/remote mode UX | 4/10 | Explicit local/remote selection exists and remote completion blocks without endpoint, certificate and authenticated session. Capability negotiation and live remote session remain pending. |
| Installer lifecycle experience | 7/10 | NSIS lifecycle harness covers interruption recovery, repair, upgrade, downgrade resistance and uninstall; polished interactive UX and release-distribution gates remain. |
| Update and recovery UX | 3/10 | Installer recovery/repair mechanics exist; in-app signed update channel, progress, rollback and recovery surfaces are pending. |
| Accessibility, packaging and release evidence | 4/10 | Automation names and exact-SHA Windows build/readiness workflows exist; accessibility regression, signed release evidence and final homologation remain. |

**Accepted desktop/product score: 58/100 (58%).**

The accepted delta from the previous Desktop/Product score is **+3 percentage points**. The increase is deliberately limited to the product evidence justified by the real Projects credential-management surface and its security UX. The project credential Core and login coordinator are important enabling capabilities, but they do not earn additional Desktop/Product points until a concrete browser adapter and user-visible E2E execution are proven. This percentage must not be substituted for the global AEVRIX homologation percentage.

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
- Settings/Security exposes installation ID, first-run state, operating mode, local identity, EngineHost proof and remote state, without unsafe bypass toggles.

## Accepted increment — Project-local credentials

### Secure vault foundation — PR #405

Exact tested head: `18334ad900c44fe99711195fa903ecdd6b595f0f`.

Implemented:

- project-scoped credential metadata keyed by project + canonical HTTPS login URL + credential identity;
- multiple accounts for the same login URL and explicit default arbitration;
- Windows Credential Manager backend with local-machine persistence;
- usernames/passwords absent from `project.json`, generic logs and exported metadata;
- strict project isolation;
- disposable secret leases with buffer clearing;
- browser profile path isolated by project + target;
- authorized-autofill broker blocks secret access before policy approval.

Windows gates passed: Source Policy, Desktop Release build, Windows Core, Remote Security and Orchestrator Judge. Windows Core included a real Credential Manager write/read/delete round trip.

### Projects credential-management UX — PR #421

Exact tested head: `3a1925d61b7333dc5e01e45043e83bb4edaca5ea`.  
Promotion merge result: `c9c5ef1c28e2bc8ed2ebc198dc892d5649d008f5`.

Implemented and accepted:

- `Projetos` is a real routed Desktop surface rather than a placeholder;
- user selects a local project and registers account label, HTTPS login URL, username and password;
- multiple accounts may coexist for the same URL;
- one account can be set as the default for that URL;
- the persistent list exposes only label, URL and default/alternative state, never password and not the stored username;
- username/password entry fields are cleared after save or failure;
- deletion requires explicit confirmation;
- the UI states that secrets remain on the current PC and MFA/2FA remains a separate gate;
- the integration preserves the newer FirstRunWindow flow and avoids rewriting the large MainWindow surface by using an isolated partial class.

Windows gates passed: Source Policy, Desktop Release build, Windows Core, Remote Security and Orchestrator Judge.

### Governed browser-login coordinator — PR #423

Exact tested head: `f29fd837ad23c9b2c59b4139dbb4a8c83ce3b8ab`.  
Promotion merge result: `877d6c205d77b16671cf24563bc905995f471593`.

The coordinator is accepted as a Core capability:

- requires authorized project execution and authorized credential autofill;
- requires credential persistence in Research Browser policy;
- automatic relogin additionally requires explicit `AutomaticRelogin` policy;
- validates recipe target and login host allowlist before any secret read;
- returns selection-required rather than guessing when multiple accounts have no unique default;
- performs navigate/fill/submit through a secret-aware adapter contract;
- keeps the credential in a disposable lease and zeroes it on success or browser failure;
- a hostile-path test captures the same password memory in the adapter, forces submit failure and confirms the captured buffer is zeroed afterward.

Windows gates passed: Source Policy, Desktop Release build, Windows Core, Remote Security and Orchestrator Judge.

**Not yet accepted as browser E2E:** the repository does not yet contain a concrete Chromium/Playwright/WebView implementation of `IResearchBrowserLoginFormAdapter`. No E2E browser-login points are awarded until that adapter exists and passes authorized-page tests.

## Earlier exact Windows evidence

PR #365 exact head `589c63f36bcb805c786deb7c0cbf201a794df1ba` passed Source Policy, Desktop Release build, Windows Core, Remote Security and Orchestrator Judge.

The first-run post-merge candidate also passed the exact-SHA Desktop Build Evidence and Windows Readiness V2 smoke/soak/recovery chain. The installer-lifecycle import in that particular readiness run was `skipped` because no installer lifecycle artifact existed for that exact Desktop merge SHA; it was not converted into a false PASS.

## Next autonomous priorities

1. Implement and benchmark a concrete Research Browser host/adapter before choosing Chromium/Playwright/WebView as a permanent dependency; require authorized-page E2E login, MFA detection, logout/relogin and secret-redaction evidence.
2. Connect device enrollment and authenticated remote session to first-run through the existing secure transport, using governed/signed bootstrap configuration rather than free-form secret entry.
3. Connect Mission Control to the real orchestrator feed using an explicit read model and bounded event stream.
4. Connect Activity to the canonical Proof Ledger through a privacy-filtered projection while keeping raw evidence out of generic UI logs.
5. Add policy-backed OS permission/capability status to Settings/Security without exposing bypass controls.
6. Add in-app update/recovery surfaces bound to signed update metadata and rollback policy.
7. Add automated accessibility, keyboard/focus and visual regression coverage.

## Update protocol

Every completed Desktop/Product action must update this scorecard only when new repository evidence justifies a point change. Every status report must end with the accepted Desktop/Product percentage, the delta from the previous accepted score and the next highest-value blocker.
