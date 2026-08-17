# AEVRIX Mobile Integration Fabric v0.3

## Purpose

This layer gives the Mobile Reverse Engineering, Reconstruction & App Lab governed access to high-value public intelligence and local analysis toolchains without creating a second orchestration, evidence, or security authority.

The Mobile Lab remains a specialist. Central AEVRIX capability governance, execution authority, evidence fusion, sandboxing, and promotion gates remain authoritative.

## Integrated surfaces

### GitHub public intelligence — read-only

The `GitHubPublicApiClient` implements bounded, version-pinned read access for:

- repository metadata;
- public release and release-asset metadata;
- GitHub Actions workflow artifact metadata used to find test/evidence bundles;
- global security advisories;
- SPDX SBOM export from the dependency graph.

Controls:

- pinned `X-GitHub-Api-Version`;
- optional `GITHUB_TOKEN`, never persisted in evidence;
- ETag/`If-None-Match` support to reduce rate usage;
- GitHub rate-limit headers normalized into the evidence envelope;
- HTTPS-only, `api.github.com` allowlist, port 443 only;
- redirects blocked at the generic external transport boundary;
- bounded response size and timeout;
- response SHA-256 and semantic request SHA-256;
- read-only API subset; no branch, workflow, secrets, token or repository mutation methods.

GitHub code search is intentionally not part of the clean-room reconstruction path. Upstream release/source metadata may be inspected for tool provenance, but target behavior must be independently observed and reconstructed from authorized evidence.

### OSV vulnerability intelligence

`OSVApiClient` supports:

- package + ecosystem + optional version queries;
- source-commit queries;
- bounded batch queries.

It is used to cross-check known vulnerabilities in candidate analysis toolchains and dependencies. Query evidence is hashed and stored without credentials.

### MobSF REST — local service only

MobSF is modeled as an out-of-process local security cross-check. Artifact submission is denied unless the endpoint resolves to loopback (`127.0.0.0/8`, `::1`, or `localhost`). This prevents accidental upload of proprietary APK/IPA evidence to third-party infrastructure.

MobSF remains `CANDIDATE / UNMEASURED` until licensing, containment, accuracy, duplication, performance and evidence quality are benchmarked.

### Local toolchain probes

`LocalToolProbe` discovers and fingerprints supported binaries without installing or promoting them. Current probes include:

- JADX;
- Apktool;
- ADB;
- `apkanalyzer`;
- bundletool wrapper/CLI where provided by the host;
- Frida CLI;
- mobsfscan.

Every located executable is resolved to a real path, SHA-256 fingerprinted, and queried for version with `shell=False`, a bounded timeout, and bounded output. `NOT_FOUND` is valid evidence, not a fabricated failure score.

### Androguard

Androguard is registered as a complementary local Python analysis candidate for APK/DEX parsing, call graphs, CFG, manifest/resources and certificate inspection. It must benchmark against JADX/Apktool/Android SDK outputs before promotion.

### Frida — observation-only boundary

Frida can materially improve runtime understanding, but it is more powerful than this specialist requires. The integration therefore has a fail-closed `InstrumentationObservationPolicy`.

Allowed classes of work are enumeration, attachment for observation, call tracing, block tracing and metadata reads. Mutation/evasion requests and scripts containing bypass/disable/patch/write-memory/authentication/MFA/CAPTCHA/certificate-pinning/signature/anti-tamper/integrity/root- or jailbreak-detection behavior are rejected before an adapter can receive them.

This is an architectural boundary, not just a UI convention.

## Evidence envelope

Every HTTP integration result records:

- source ID;
- operation;
- requested and returned URL;
- HTTP status;
- UTC observation timestamp;
- request SHA-256 excluding credentials/headers;
- response SHA-256;
- response byte count;
- ETag when present;
- GitHub rate-limit snapshot when present.

External evidence remains evidence; it never promotes a capability by itself.

## Lifecycle

All new capabilities start as `CANDIDATE / UNMEASURED / hard_gates=PENDING`.

Promotion requires measured evidence through the existing AEVRIX capability governance dimensions: efficacy, precision gain, auditability, security controllability, necessity, reliability, credibility/maturity, cost-benefit, performance, maintainability, interoperability and uniqueness.

No percentage or score is inferred from tool popularity, stars, documentation quality or mere installation success.

## Operational CLI

```text
python -m tools.mobile_lab.integration_cli inventory
python -m tools.mobile_lab.integration_cli probe-local
python -m tools.mobile_lab.integration_cli github-releases skylot jadx
python -m tools.mobile_lab.integration_cli github-sbom OWNER REPO
python -m tools.mobile_lab.integration_cli osv-package PyPI requests 2.31.0
```

Live network commands are explicit. Unit tests use injected transports and do not depend on Internet availability.

## Next benchmark gates

1. Pin exact tool releases and executable hashes from official upstream sources.
2. Run a controlled Android corpus through JADX + Apktool + Androguard + `apkanalyzer` and compare structural recall, disagreement and runtime cost.
3. Run MobSF locally against the same corpus and measure incremental security findings versus duplication/noise.
4. Bind ADB/AVD/UIAutomator to the existing disposable device contract and collect canonical `ObservationRecord` evidence.
5. Add Frida only to authorized observation experiments where static + normal dynamic evidence leaves a measurable gap.
6. Feed measured results into `capability-registry.json`; do not promote any candidate before hard gates pass.
