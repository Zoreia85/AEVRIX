# AEVRIX

**Governed technical intelligence for complex software research.**

AEVRIX is an open-source platform for authorized, clean-room technical research. One governed intelligence core is exposed through three coordinated product surfaces: a native Windows workstation, a Developer API/SDK platform and a secure mobile companion.

> **Release status:** active development — **NOT_HOMOLOGATED**. Screens, flows and product surfaces may be shown as design concepts before their production implementation is complete. No build is called homologated until the documented AVA release gates have real evidence for the exact delivered artifact.

## Three ways to use AEVRIX

### AEVRIX Desktop — Windows

The primary workstation for deep authorized research. It coordinates isolated local runtime components, research browser sessions, specialists/adapters, evidence capture, proof-ledger provenance and Project Blueprint synthesis from one command surface.

### AEVRIX Developer Platform — API / SDK

A governed automation surface for CI/CD, internal engineering systems and enterprise workflows. Projects, jobs, evidence and Blueprints use the same authorization and provenance model as the Desktop client; the API is not a bypass around local or remote security boundaries.

### AEVRIX Mobile Console — Android / iOS companion

A secure control surface for project monitoring, approval workflows, notifications, controlled uploads and review of evidence/Blueprint summaries. Heavy sandboxed analysis remains on appropriate desktop/remote execution environments rather than being silently moved into the phone.

## Product promise

AEVRIX is designed to turn fragmented technical observations into **traceable, governed and reproducible technical knowledge**. The platform separates observation from inference, binds evidence to the execution that produced it, isolates workspaces and keeps authority-changing operations explicit.

## Core capabilities

- Multi-specialist orchestration with fail-closed policy boundaries.
- Static, dynamic, visual/OCR, structural and network-oriented research adapters.
- Evidence provenance linked to governed executions and proof-ledger state.
- Project Blueprint synthesis with proof-bound knowledge exchange.
- Isolated Windows Engine Host and governed Research Browser.
- Workspace/user separation, privacy minimization and authenticated local/remote transport.
- Plugin/adapter architecture for multiple software domains, formats and languages.
- Remote intelligence plane with explicit capability admission and source pinning.

Capabilities are admitted only when the relevant implementation and policy gates exist. AEVRIX does not implement authentication bypass, credential/session theft, CAPTCHA bypass, DRM/license bypass, exploit deployment, malicious persistence or anti-bot evasion.

## Architecture

```text
Desktop Windows        Developer API / SDK        Mobile Console
      \                       |                       /
       \                      |                      /
        +---------- AevrixSecureTransport ----------+
                         |
                         v
                 Remote API / Device Auth
                         |
                         v
                  Orchestrator / Judge
                    /      |       \
                   /       |        \
          specialists   evidence   Blueprint
                         |
                         v
               verified knowledge / proof
```

Windows process boundary:

```text
AEVRIX.exe (WinUI 3)
    |
    | Named Pipes / CurrentUserOnly / versioned protocol / ephemeral token
    v
AEVRIX.EngineHost
    |
    v
Embedded private runtime
    +--> governed browser/research adapters
    +--> deterministic fixtures
    +--> isolated analysis workers
```

## Security and privacy principles

- Source, architecture, CI and release manifests are public by default.
- Secrets are never public: signing keys, private keys, device credentials, API credentials, target credentials and production certificates stay outside Git.
- The installed client is treated as untrusted relative to sensitive server-side authority.
- Protected operations require explicit authentication/authorization and fail closed when required online authority is unavailable.
- Workspace, user and execution provenance are kept distinct.
- Personal or restricted data is minimized and kept inside the narrowest admissible boundary.
- Evidence classes remain distinct: `Observed`, `ExperimentallyValidated`, `Inferred`, `VendorClaim`.

## Product experience

The visual and interaction system is being developed as a dark-first, high-trust technical interface with explicit system health, mission state, evidence confidence and security posture. The canonical screen order and UX constraints are documented in [`docs/product/AEVRIX_PRODUCT_SURFACES_AND_UX.md`](docs/product/AEVRIX_PRODUCT_SURFACES_AND_UX.md).

Concept visuals are not evidence of release readiness. Production screenshots will be linked only to builds that can be reproduced and tested.

## Repository layout

- `apps/aevrix-windows` — Windows client core, Engine Host and installer/runtime work.
- `apps/aevrix-mobile` — mobile companion work as migration is completed.
- `services/aevrix-remote-brain` — remote security/orchestration services.
- `tools/research-lab` — governed browser capture and deterministic fixtures.
- `docs` — architecture, security, product UX and validation rules.
- `.github/workflows` — public CI using standard GitHub-hosted runners.

## AEVRIX release gate

The word **HOMOLOGATED** is prohibited unless the exact delivered build passes at least:

1. real Windows build;
2. clean installation;
3. first launch;
4. mandatory backend connection/fail-closed behavior;
5. Research Browser and Engine Host;
6. embedded private runtime;
7. repair;
8. uninstall;
9. upgrade;
10. failure recovery;
11. critical security regressions;
12. Authenticode for the externally distributed Windows release;
13. AVA visual/functional validation on clean Windows;
14. regression tests and exact release hashes.

The artifact that is tested is the artifact that must be released. No rebuild after final validation.

## Development

The baseline SDK is pinned in `global.json`. Development targets .NET 10. Windows UI targets WinUI 3 / Windows App SDK. Research tooling uses the repository-pinned Python runtime.

Public CI intentionally uses standard GitHub-hosted runners. Larger runners are not part of the default pipeline.

## License

Apache License 2.0. See `LICENSE` and `NOTICE`.

Third-party dependencies and optional adapters keep their original licenses; see `THIRD_PARTY_NOTICES.md` and the release SBOM/license inventory.
