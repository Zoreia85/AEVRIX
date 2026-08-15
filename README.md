# AEVRIX

**Open-source technical intelligence for Windows, mobile and governed research.**

AEVRIX is an open-source platform for authorized, clean-room technical research. It combines a native Windows desktop client, an isolated research engine, evidence provenance, Project Blueprint synthesis, an optional Android node, and a remote intelligence plane.

> **Status:** active development — **NOT_HOMOLOGATED**. The repository may compile at different points in development, but no release is considered homologated until the documented AVA gates have real evidence.

## Principles

- Open source by default: source code, architecture, CI and release manifests are public.
- Secrets are never public: signing keys, private keys, device credentials, API credentials, target credentials and production certificates stay outside Git.
- The installed client is treated as untrusted. Sensitive authority remains server-side and every protected operation is explicitly authenticated and authorized.
- Normal product operation requires Internet. Offline mode is fail-closed for protected capabilities and may expose only diagnostics/cache explicitly allowed by policy.
- Research is limited to public or legitimately authorized targets. AEVRIX does not implement authentication bypass, credential/session theft, CAPTCHA bypass, DRM/license bypass, exploit deployment, malicious persistence or anti-bot evasion.
- Evidence classes remain distinct: `Observed`, `ExperimentallyValidated`, `Inferred`, `VendorClaim`.

## Architecture

```text
AEVRIX Windows / Android
        |
        v
AevrixSecureTransport
TLS + SPKI pinning + mTLS + short token + DPoP
        |
        v
Remote API / Device Auth
        |
        v
Orchestrator / Judge
        +--> providers / models
        +--> candidate knowledge
        +--> evidence validation
        +--> trusted memory (only after promotion)
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
Embedded Python worker
    +--> Playwright
    +--> private Chromium
    +--> Crawl4AI
    +--> Research Lab
```

## Repository layout

- `apps/aevrix-windows` — Windows client, core, Engine Host and installer work.
- `apps/aevrix-mobile` — Android node (added as the migration is completed).
- `services/aevrix-remote-brain` — remote security/orchestration services.
- `tools/research-lab` — governed browser capture and deterministic fixtures.
- `docs` — architecture, security and validation rules.
- `.github/workflows` — public CI using standard GitHub-hosted runners.

## AEVRIX 0.001 release gate

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

The baseline SDK is pinned in `global.json`. Development targets .NET 10. Windows UI uses WinUI 3 / Windows App SDK. Research Lab uses Python 3.13.x.

Public CI intentionally uses standard GitHub-hosted runners. Larger runners are not part of the default pipeline.

## License

Apache License 2.0. See `LICENSE` and `NOTICE`.

Third-party dependencies and optional adapters keep their original licenses; see `THIRD_PARTY_NOTICES.md` and the release SBOM/license inventory.
