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

`AdaptiveModelCouncilProvider` connects that fabric to the model-analysis path. It consumes broker ranking, invokes only registered implementations, enforces a bounded attempt budget, records success/failure telemetry, rejects provider-identity spoofing and fails over deterministically. Caller cancellation is never converted into failover. This allows local models, remote models and future specialist/quantum-backed analyzers to compete as replaceable council members without becoming the AEVRIX brain.

```text
AnalysisTask
  -> CapabilityBroker rank
  -> Adaptive Model Council
       -> provider #1
       -> bounded failover to #2/#3 on provider failure
  -> ModelAnalysisCandidate
  -> OrchestratorJudge
  -> evidence validation
  -> trusted-memory promotion only after validation
```

The broker and council control tool selection only. They do not promote provider output into trusted memory; Judge/evidence validation remains mandatory.

### Mission Director / specialist swarm

`MissionDirector` is the bounded scheduler above individual analysis specialists. A mission is an acyclic dependency graph of tasks associated with one project and one target. Each task declares the specialist capability, objective, allowed evidence boundary, dependencies and whether the task is required.

Initial specialist taxonomy is domain-agnostic:

- static analysis;
- dynamic analysis;
- vision/OCR;
- network behavior;
- structural analysis;
- documentation;
- reconstruction;
- quantum/hybrid experiments.

The scheduler validates the entire graph before execution, fails closed if a required specialist is unavailable, enforces a bounded concurrency budget, blocks dependent work after failed prerequisites and preserves deterministic result ordering. A specialist cannot cite evidence outside the evidence boundary assigned to its task. Specialist failure is recorded as an execution result and cannot silently become trusted knowledge.

The Mission Director deliberately does not embed a model runtime or a particular tool. Capability-brokered councils, local models, remote models, deterministic analyzers and future quantum/hybrid solvers can implement specialist roles through adapters. Their outputs still flow through provenance/evidence validation and the Judge before trusted-memory promotion.

```text
mission
  -> validate DAG + evidence boundaries
  -> schedule ready specialists under concurrency budget
  -> collect bounded outputs/artifacts
  -> block dependents on failed prerequisites
  -> Evidence Bus / candidate fusion
  -> Judge
  -> trusted knowledge only after validation
```

### Governed local process execution

Pinned adapters use `GovernedOutOfProcessRuntime` as the single authority boundary for network and filesystem policy. The boundary now routes through the replaceable `IOutOfProcessIsolationBackend` contract rather than embedding one sandbox technology.

The current `LocalUnrestrictedOutOfProcessBackend` deliberately supports only `Unrestricted` network and filesystem authority. Requests for `None`, `LoopbackOnly`, `Allowlisted`, `WorkspaceOnly` or `WorkspaceReadOnly` remain fail-closed unless a registered backend can enforce the complete authority profile.

Backends are bounded, uniquely identified and deterministically ranked by priority. A backend declaration that it can enforce a policy is not sufficient: after execution, the authority boundary independently checks the returned attestation and rejects a result that fails to prove required network or filesystem isolation. This creates the insertion point for future AppContainer/restricted-token, container and VM implementations without weakening the default local-process path.

```text
Pinned executable + SHA-256
  -> unified network/filesystem authority policy
  -> isolation backend selection
       -> local-unrestricted (current)
       -> AppContainer/restricted token (future)
       -> container (future)
       -> VM (future)
  -> deny before launch when no backend can enforce the full policy
  -> backend execution
  -> independent attestation check
  -> Evidence Boundary / Judge remain downstream
```

The authority decision and backend attestation are execution-security metadata, not evidence about the analyzed target. A future backend may only enable constrained scopes after tests demonstrate real OS/runtime enforcement.

### Evidence Bus / candidate fusion

`EvidenceBus` is a project-scoped bus for structured specialist observations, not a raw evidence or secret store. Each observation is bound to one project, target, source task and specialist and carries a bounded claim key/value, observation class, sensitivity, confidence, SHA-256 content hash, source artifacts and parent evidence ids.

The specialist publication path enforces the Mission Director evidence boundary. Observation ids are immutable: an idempotent re-publication is accepted, while rebinding the same id to different content fails closed. Raw credentials, access tokens, private keys and session secrets are rejected. Personal data cannot be classified as public, and only sanitized public `Observed`/`ExperimentallyValidated` observations are eligible for any future global-learning export.

`EvidenceFusionEngine` performs deterministic claim-level fusion. Equal normalized values from independent tasks and independent specialist kinds can become `Convergent`; a single-source result remains `Insufficient`; multiple values for the same claim become `Contested`. Contested fusion never selects a winner implicitly. Observation class weights vendor claims and inference below directly observed or experimentally validated evidence.

```text
specialist observations
  -> project/target/provenance boundary validation
  -> EvidenceBus
  -> group by governed claim key
  -> independent-source + independent-specialist scoring
  -> Convergent | Insufficient | Contested
  -> EvidenceFusionCandidate
  -> Judge / independent validation
  -> trusted knowledge only after explicit promotion
```

A fusion candidate always requires Judge validation. Cross-project, cross-target and cross-claim fusion is rejected rather than silently filtered.

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
