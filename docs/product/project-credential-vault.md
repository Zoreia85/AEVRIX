# AEVRIX — Project Credential Vault

Status: **IN DEVELOPMENT — NOT HOMOLOGATED**

## Product intent

Each AEVRIX project may register one or more login identities for authorized systems so a governed browser/capture workflow can authenticate automatically when it reaches the corresponding login page.

The credential scope is:

`ProjectId + canonical HTTPS login URL + CredentialId`

The human-facing account label distinguishes multiple identities even when the same application uses a single login URL.

## Storage boundary

Credentials are local-only by design:

- passwords and usernames are never written to `project.json`;
- passwords and usernames are never written to generic logs, Proof Ledger summaries, exported project packages or cloud synchronization payloads;
- the non-secret local index lives under the per-user AEVRIX Vault root;
- the secret payload is stored through Windows Credential Manager as a generic credential with `LOCAL_MACHINE` persistence;
- the Windows Credential Manager target name contains only opaque project/credential identifiers, not the login URL, username or password;
- missing local secret material is a fail-closed condition;
- project browser-session state is isolated by project + target rather than target alone.

## URL binding

Login URLs must be absolute HTTPS and cannot contain embedded credentials. Query strings and fragments are intentionally removed from the canonical matching identity so transient return URLs, CSRF navigation parameters or fragments do not create duplicate accounts.

The canonical match remains path-specific. `https://example.com/login` and `https://example.com/admin/login` are different credential targets.

## Multiple accounts

A project can contain multiple credentials for the same canonical login URL.

- if exactly one credential matches, it can be selected;
- if several match and exactly one is marked default, the default can be selected automatically;
- if several match and no unique default exists, resolution returns `Ambiguous` and automation must not guess;
- setting a new default automatically revokes the previous default for that same canonical login URL only;
- the Desktop shows a human account label and never needs to re-display the stored password.

## Runtime authorization

Saving a credential does not grant unconditional access to it. Automatic login requires both:

1. the project execution/scan is currently authorized; and
2. credential autofill is authorized for that project execution.

`ProjectCredentialAutofillBroker` blocks before reading the secret store when either gate is false. `ProjectResearchBrowserLoginCoordinator` then adds the Research Browser constraints: validated `LoginRecipe`, matching target, explicit host allowlist, `RememberCredentials=true` and, for automatic relogin, `AutomaticRelogin=true`.

## Browser-session isolation

Local-only credential storage is not sufficient by itself because an authenticated browser also persists cookies, localStorage, IndexedDB and other session state. Persistent browser state for an authenticated project is therefore isolated by both project and target.

The canonical project profile path is:

`BrowserProfiles/<ProjectId>/<TargetId>`

Two projects pointing to the same portal do not share a browser profile merely because they have the same target identifier. `AevrixDataPaths.ProjectBrowserProfile(projectId, targetId)` provides that boundary.

## Runtime handling

Secret material is retrieved only when a matching authorized login operation needs it. The Core exposes it through a disposable credential lease and clears the lease's managed character buffers on disposal.

The login coordinator keeps the secret lease only through navigation/fill/submit. Tests also force a browser-submit exception after the password memory has been handed to the adapter and verify that the same captured memory is zeroed after failure.

Browser adapters must never log field values, passwords, raw credential payloads or screenshots that expose secrets without the existing evidence/privacy policy explicitly permitting a protected capture.

## MFA / 2FA

Password autofill does not imply bypassing MFA. One-time codes, hardware-key prompts, passkeys, biometric approvals and other second-factor challenges remain independent authentication gates. A workflow encountering such a gate must pause, request the permitted human/device interaction, or use a future separately governed MFA capability.

## Accepted implementation

### Secure local foundation — PR #405

- `ProjectCredentialVault.cs` — project-scoped metadata, canonical URL resolution, default-account arbitration and disposable secret lease;
- `WindowsCredentialManagerProjectSecretStore.cs` — Windows Credential Manager backend with local-machine persistence and opaque target names;
- `ProjectCredentialAutofillBroker.cs` — fail-closed bridge preventing automatic secret retrieval outside an authorized execution;
- `AevrixDataPaths.ProjectBrowserProfile` — project + target browser-profile boundary.

Exact tested head: `18334ad900c44fe99711195fa903ecdd6b595f0f`.

Windows gates passed: Source Policy, Desktop Release build, Windows Core, Remote Security and Orchestrator Judge. The Windows Core suite included a real Credential Manager write/read/delete round trip.

### Project credential-management UX — PR #421

The `Projetos` route now provides a real credential-management surface:

- project selector;
- account label;
- HTTPS login URL;
- login/user field;
- password field;
- multiple accounts per URL;
- explicit default account;
- local deletion confirmation;
- credential list exposes only label, URL and default/alternative state;
- username/password fields are cleared after save or failure;
- UI states that secrets remain on the current PC and MFA remains separate.

Exact tested head: `3a1925d61b7333dc5e01e45043e83bb4edaca5ea`.

Windows gates passed: Source Policy, Desktop Release build, Windows Core, Remote Security and Orchestrator Judge.

### Governed login coordinator — PR #423

`ProjectResearchBrowserLoginCoordinator` now defines the operational bridge from an authorized project credential to a Research Browser form adapter:

- blocks before secret access when execution/autofill policy is not authorized;
- validates Research Browser policy, recipe target and login host;
- resolves one/default account or returns explicit selection-required state;
- navigates to the validated login URL when necessary;
- passes user/password through `ReadOnlyMemory<char>` to the adapter;
- submits using the recipe selector;
- disposes and zeroes the lease on success or browser failure.

Exact tested head: `f29fd837ad23c9b2c59b4139dbb4a8c83ce3b8ab`.

Windows gates passed: Source Policy, Desktop Release build, Windows Core, Remote Security and Orchestrator Judge.

## Remaining integration gate

The repository still does **not** contain a concrete Chromium/Playwright/WebView host implementing `IResearchBrowserLoginFormAdapter`. Therefore the coordinator and UI are accepted, but real browser E2E autofill is **not yet homologated**. The next gate is a concrete browser adapter plus authorized-page tests, including MFA detection, logout/relogin and secret-redaction evidence.

Project-local auth cleanup is being developed separately so future project deletion can remove the project's Credential Manager entries and browser-session directory without touching another project.
