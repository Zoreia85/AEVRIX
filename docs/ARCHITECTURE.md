# AEVRIX Architecture

## Product model

AEVRIX is open-source software with a distributed trust model. Publishing the source does not require publishing operational secrets. The desktop/mobile client is deliberately treated as an untrusted endpoint.

### Windows

- C# / .NET 10
- WinUI 3 / Windows App SDK
- `AEVRIX.exe`
- `AEVRIX.Core`
- `AEVRIX.EngineHost`
- Named Pipes with `CurrentUserOnly`, protocol versioning and ephemeral session token
- subordinate Python 3.13.x worker
- Playwright, private Chromium and Crawl4AI
- Windows Job Object with `KILL_ON_JOB_CLOSE`
- user data under `%LOCALAPPDATA%\AEVRIX`

### Remote plane

Protected modules use `AevrixSecureTransport` only.

Target stack:

- normal TLS validation;
- SPKI SHA-256 pinning with current + backup rotation pins;
- mTLS/device certificate;
- short-lived access token;
- DPoP ES256;
- server nonce;
- `jti` anti-replay;
- exact request body SHA-256;
- server-side entitlement.

### Device enrollment

```text
first boot
  -> generate ECDSA P-256 key
  -> prefer Microsoft Platform Crypto Provider / TPM
  -> non-exportable DigitalSignature key
  -> PKCS#10 CSR
  -> /device/enroll
  -> policy/license/device validation
  -> AEVRIX CA certificate
  -> bind certificate to local private key
```

No PFX/private key is shipped in an installer. A lower-security non-TPM mode, if ever supported, must be explicit and visible.

### DPoP proof

Each protected request binds:

- `jti`
- `iat`
- `htm`
- `htu` without query/fragment
- `ath`
- server `nonce`
- `bh` (SHA-256 of exact body)

Server validation uses an initial proof age around 90 seconds and replay storage keyed by `SHA-256(jti)`.

### Orchestrator / Judge

Model output is never trusted memory by itself:

```text
task -> model/provider -> candidate knowledge -> evidence/comparison/test -> validation -> trusted memory
```

The capability fabric is provider-independent. `CapabilityBroker` ranks approved providers from bounded telemetry (quality, reliability, latency, health and consecutive failures). Unapproved, disabled, stale, unavailable or quarantined providers are excluded fail-closed. Repeated failures demote a provider and force selection of a healthy backup; a later successful health probe can recover an unavailable provider, while quarantine always requires an explicit release.

This broker controls tool selection only. It does not promote provider output into trusted memory; Judge/evidence validation remains mandatory.

### Evidence to Blueprint

```text
Research Capture
 -> manifest/integrity verification
 -> EvidenceStore
 -> structured extraction
 -> architecture/workflows/API/UI
 -> behavioral models (only with experiments)
 -> Reproduction Readiness
 -> ProjectBlueprint.Validate()
 -> exporter
```

Coverage percentages require a defensible denominator. Unknown coverage is not converted into a fabricated high percentage.
