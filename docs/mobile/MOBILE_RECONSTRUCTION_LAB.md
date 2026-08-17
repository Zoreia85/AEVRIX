# AEVRIX Mobile Reverse Engineering, Reconstruction & App Lab

Status: foundational vertical slices v0.1 + v0.2.

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

## v0.2 — observation, disposable lifecycle and inference foundation

The second vertical slice adds four contracts while preserving capability governance:

- `ObservationRecord`: timezone-aware, hash-linked dynamic evidence records for state transitions, screenshots, UI trees, logs, network traces, persistence snapshots and resource metrics;
- `DisposableLabRunner`: adapter-neutral lifecycle enforcement for `create -> boot -> probe -> destroy`, including mandatory explicit authorization and cleanup on both success and failure;
- `Algorithm Inference`: conservative black-box numeric inference with constant, proportional and affine rule families, ambiguity detection and automatic selection of discriminating test cases;
- `NumericDifferential`: tolerance-aware Original × Reconstruction numeric comparison.

The inference engine deliberately does **not** call a finite sample a mathematical proof. `PROVEN_WITHIN_DECLARED_DOMAIN` is possible only when the caller explicitly declares that the supplied cases exhaust a finite domain and exactly one tested rule family explains all cases within tolerance. Otherwise the engine emits `HIGHLY_PROBABLE`, `INFERRED`, `INFERRED_AMBIGUOUS`, or `UNEXPLAINED`.

The disposable runner is not itself an emulator. Android SDK and Apple simulator implementations remain capability adapters and stay unavailable to this runner until governance permits their lifecycle state.

## Planned next vertical slices

1. Android manifest/resource adapters behind sandboxed capability contracts.
2. Governed Android SDK adapter implementing the disposable lifecycle after benchmark/hard-gate approval.
3. Evidence Bus adapter for raw screenshots, UI trees, logs, traces and persistence snapshots.
4. Algorithm inference corpus expansion: boundary-value, metamorphic, piecewise, rounding and classification families.
5. Canonical Mobile Blueprint and richer Original × Reconstruction comparator.
6. iOS simulator adapter on compliant Apple infrastructure after governance approval.

## Homologation semantics

`HOMOLOGATION_CANDIDATE` is not equivalent to a release approval. Final homologation belongs to the AEVRIX Judge/QA authority and requires complete evidence, zero unresolved critical divergence and all applicable security/governance gates.
