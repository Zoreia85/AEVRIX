# Security Policy

## Supported versions

AEVRIX is pre-0.001 and has no production-supported release yet. Security issues affecting the development branch are still treated as high priority.

## Reporting a vulnerability

Do **not** open a public issue containing an exploitable vulnerability, secret, private key, credential, session material, private target data or personally identifying evidence.

Until a private disclosure channel is published in the repository settings, report only a minimal non-sensitive notice asking maintainers for a private channel. Never paste credentials or working exploit material into a public GitHub issue.

## Non-negotiable security invariants

- No secrets in Git, build logs, screenshots or release artifacts.
- No master API keys or backend private keys in desktop/mobile clients.
- `AevrixSecureTransport` is the only approved protected HTTP transport abstraction.
- TLS validation remains enabled when SPKI pinning is used.
- Device private keys are non-exportable when the platform supports the required security policy; no silent downgrade to exportable identity keys.
- Short-lived authorization plus DPoP/nonce/replay protection for protected requests.
- Research Browser target scope is explicit and fail-closed.
- Evidence integrity is SHA-256 based and references must resolve to existing evidence.
- Quarantined evidence is excluded from normal Blueprint promotion.
- Updates require immutable provenance, integrity validation and promotion gates.
- Production signing keys are never committed.

## Scope boundary

AEVRIX is for authorized/public clean-room analysis. Contributions implementing credential theft, session theft, unauthorized access-control bypass, CAPTCHA bypass, DRM/license bypass, exploit deployment, malicious persistence or security-control evasion will not be accepted.
