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
- missing local secret material is a fail-closed condition.

## URL binding

Login URLs must be absolute HTTPS and cannot contain embedded credentials. Query strings and fragments are intentionally removed from the canonical matching identity so transient return URLs, CSRF navigation parameters or fragments do not create duplicate accounts.

The canonical match remains path-specific. `https://example.com/login` and `https://example.com/admin/login` are different credential targets.

## Multiple accounts

A project can contain multiple credentials for the same canonical login URL.

- if exactly one credential matches, it can be selected;
- if several match and exactly one is marked default, the default can be selected automatically;
- if several match and no unique default exists, resolution returns `Ambiguous` and automation must not guess;
- setting a new default automatically revokes the previous default for that same canonical login URL only.

## Runtime handling

Secret material is retrieved only when a matching authorized login operation needs it. The Core exposes it through a disposable credential lease and clears the lease's managed character buffers on disposal.

Browser adapters must never log field values, passwords, raw credential payloads or screenshots that expose secrets without the existing evidence/privacy policy explicitly permitting a protected capture.

## MFA / 2FA

Password autofill does not imply bypassing MFA. One-time codes, hardware-key prompts, passkeys, biometric approvals and other second-factor challenges remain independent authentication gates. A workflow encountering such a gate must pause, request the permitted human/device interaction, or use a future separately governed MFA capability.

## Current implementation

Core:

- `ProjectCredentialVault.cs` — project-scoped metadata, canonical URL resolution, default-account arbitration and disposable secret lease;
- `WindowsCredentialManagerProjectSecretStore.cs` — Windows Credential Manager backend with local-machine persistence and opaque target names.

Tests cover:

- URL canonicalization;
- absence of username/password from local metadata;
- strict project isolation;
- multiple accounts and default selection;
- stable matching across transient query/fragment changes;
- fail-closed missing secret behavior;
- corrupt registry handling;
- disposed lease access rejection;
- real Windows Credential Manager save/read/delete round trip on Windows CI.

## Next integration gate

The next product step is to connect this vault to the project detail UX and Research Browser login adapter. Automatic fill must only occur inside an authorized project execution and only for a URL that resolves through this vault. No generic global-password-manager behavior is permitted.
