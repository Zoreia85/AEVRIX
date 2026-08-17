# AEVRIX Mobile Reverse Engineering, Reconstruction & App Lab

Status: foundational vertical slice (v0.1).

## Boundary

This is a specialist subsystem of AEVRIX, not a second control plane. Execution authority, Evidence Bus, Judge, security policy and capability governance remain sovereign AEVRIX services.

The lab is restricted to authorized analysis and clean-room reconstruction. It does not implement DRM removal, authentication/MFA/CAPTCHA bypass, signature bypass, credential extraction or access-control circumvention.

## v0.1 implemented

- content-hash (SHA-256) for every ingested artifact;
- structure-based APK/AAB/XAPK/IPA recognition before extension hints;
- ZIP inventory without extraction or code execution;
- zip-slip/path traversal detection;
- archive entry, expanded-size and compression-ratio safety gates;
- bounded Info.plist metadata reading for authorized IPA bundles;
- deterministic Behavioral State Graph primitives;
- reconstruction scorecard that emits `UNMEASURED` instead of fabricated percentages;
- critical-divergence homologation gate;
- standard-library-only implementation to keep the initial trust surface minimal.

## Trust pipeline

`Artifact -> hash -> safe inventory -> structural classification -> evidence -> canonical model`

No analyzer is allowed to execute an artifact during this phase.

## Next governed capability cohort

The following are candidates only until benchmarked through `docs/qa/CAPABILITY_CONTINUOUS_EVALUATION.md`:

- JADX: Android DEX/static code comprehension challenger;
- Apktool: Android resources/manifest reconstruction challenger;
- Android SDK Emulator/ADB/UIAutomator: disposable dynamic lab candidate;
- Apple simctl/xcrun tooling: simulator orchestration candidate on authorized Apple hosts.

A candidate does not receive production authority merely because it is widely used. Each must earn a lifecycle state from reproducible evidence and hard-gate checks.

## Planned next vertical slices

1. Android manifest/resource adapters behind sandboxed capability contracts.
2. Disposable AVD lifecycle with deterministic snapshot/reset and evidence capture.
3. Dynamic observation record: timestamp, prior state, action, next state, screenshots, UI tree, logs and resource metrics.
4. Algorithm inference corpus and differential harness.
5. Canonical Mobile Blueprint and Original × Reconstruction comparator.
6. iOS simulator adapter on compliant Apple infrastructure.

## Homologation semantics

`HOMOLOGATION_CANDIDATE` is not equivalent to a release approval. Final homologation belongs to the AEVRIX Judge/QA authority and requires complete evidence, zero unresolved critical divergence and all applicable security/governance gates.
