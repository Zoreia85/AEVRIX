# AEVRIX — Operational Product Model

Status: implementation contract
Date: 2026-08-19
Authority: AEVRIX Comando Geral

## Product promise

AEVRIX is an evidence-to-action reverse-engineering workspace, not a single-investigation wizard. One installed station may operate several authorized investigations concurrently, constrained by measured local capacity and explicit policy.

## Four user-visible work strategies

1. **Investigate** — collect, normalize, correlate, verify and produce an auditable blueprint.
2. **Investigate + Emulate** — perform investigation plus governed execution/emulation/sandbox testing when the target is executable software or an installable application.
3. **Investigate + Build in parallel** — investigation agents keep collecting evidence while reconstruction agents implement only evidence-backed, clean-room work packages that have passed the promotion threshold.
4. **Reconstruct / Whitelabel** — start from a sufficiently investigated blueprint and build a clean-room equivalent with configurable brand, logo, colors and product naming. No proprietary source, trademarked assets or unauthorized secrets are copied.

## Target classes

- Windows / desktop application
- Mobile application (Android; iOS contract remains capability-gated)
- Website / online system
- API / service
- Repository / source package supplied with authorization
- File / document / evidence set
- Other authorized target

Executable targets must support one or more input artifacts (installer, executable, package, dependencies, configuration, sample data and authorized documentation). Online targets use HTTPS entry points and project-scoped browser/session policy.

## Investigation definition

Every investigation must bind:

- target class;
- work strategy;
- authorization class;
- workspace/project identity;
- target URL/path/artifacts;
- user goal;
- data sensitivity;
- clean-room / whitelabel policy where relevant;
- requested concurrency priority;
- resource budget selected by the scheduler;
- immutable evidence/provenance identifiers.

Defaults may reduce data entry. Authorization, privilege elevation, security downgrades and destructive actions are never inferred.

## Concurrent operation

AEVRIX may run multiple investigations at once. Capacity is not represented by a hard-coded marketing number. The station computes a conservative concurrency recommendation from CPU, logical cores, available memory and active workload. Each investigation receives a resource budget and can be queued, running, paused, blocked, failed, completed or cancelled.

The scheduler must prefer stable completion over maximum concurrency and must be able to reduce parallelism when the computer is under pressure.

## Mission dashboard

The Command Center / Mission Control must expose, at minimum:

- GitHub connection state;
- EngineHost state;
- remote brain state when configured;
- active / queued / paused / blocked / failed / completed investigations;
- per-investigation strategy and target type;
- current phase;
- progress percentage derived from weighted completed gates/work packages;
- ETA as an estimate with confidence/availability, never fabricated when insufficient samples exist;
- last activity timestamp;
- primary blocker and recommended next action;
- pause/resume/cancel controls only when backed by the orchestrator contract.

## Progress semantics

Progress is evidence-backed, not a cosmetic timer. Each investigation plan is decomposed into weighted stages. Percentage = completed verified weight / total planned weight. Replanning may alter the denominator; the UI records that a re-estimation occurred. ETA is unavailable until enough throughput history exists.

Suggested top-level stages:

1. intake & authorization;
2. acquisition / ingestion;
3. static analysis;
4. dynamic observation / emulation where applicable;
5. evidence correlation;
6. blueprint synthesis;
7. differential validation;
8. reconstruction / whitelabel when requested;
9. final QA / evidence package.

## Emulation layer

Emulation is a higher-capability layer for executable targets. It may install and run authorized applications in governed environments, exercise workflows, capture behavior and compare observed results. It must remain isolated from the clean-room implementation boundary and must preserve evidence provenance.

## Reconstruction / Whitelabel boundary

AEVRIX reconstructs behavior from an approved clean-room specification. It must not copy proprietary source code, embedded secrets, protected logos, names, trademarked assets or other non-authorized expression. Branding becomes a first-class configuration package (name, logo, colors, typography and product metadata) applied to the new implementation.

## GitHub integration

Installed AEVRIX must use GitHub authentication appropriate for a desktop application. Preferred user authorization path: GitHub App/OAuth Device Flow. No client secret is embedded in the desktop binary. Tokens are stored only in the local OS credential vault. Minimum repository permissions are requested for the enabled operation. Actions read/write is capability-gated.

The dashboard must report authentication, repository binding, canonical branch/SHA, Actions health and last successful synchronization. Network absence or authentication loss must become an explicit degraded/blocked state, not a fake healthy state.

## Windows trust and signing

Unsigned/self-signed public release artifacts are not acceptable. Release packaging must be Authenticode-signed by a trusted code-signing identity and timestamped before public distribution. Signing is a release gate and must be verified after signing. Developer/test artifacts may remain explicitly unsigned but must never be represented as production-ready.

## Repository sanitation

Tests that prove safety, lifecycle, security, clean-room boundaries or release behavior are production engineering assets and are not removed merely because they are tests. Sanitation removes only files/branches/artifacts proven obsolete, superseded, duplicated or unreachable, with evidence recorded before deletion. By final release, the canonical repository should contain only active source, required tests, current documentation, release automation and explicitly retained evidence.