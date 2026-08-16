# AEVRIX Homologation Readiness

This document is the canonical QA/readiness control plane for the question: **"Is AEVRIX ready for use?"**

It does not implement runtime features. It consumes reproducible evidence from implementation branches, CI, Windows validation, installer validation and release artifacts.

## Current release decision

- Target assessed here: **Windows release candidate**.
- Current decision: **NOT_HOMOLOGATED / NOT_READY_FOR_GENERAL_USE**.
- Baseline date: **2026-08-16**.
- Canonical blocker tracker: GitHub issue **#197 — [AVA] Production homologation evidence blockers**.
- Mandatory release specification: `docs/VALIDATION.md`.
- Governance rule: a mandatory failed or unevidenced gate cannot be overridden by a label or subjective approval.

Android and Apple evidence are required before making homologation claims for those platforms. They do not block a Windows-only release if the release scope explicitly excludes those platforms.

## Readiness score

The readiness score measures **release/homologation readiness**, not overall feature-development completion.

Current weighted baseline: **45 / 100 = 45%**.

A score of 100 is necessary but not sufficient: every mandatory gate must also be `PASS` for the exact candidate hashes. Any mandatory `FAIL`, `BLOQUEADO`, or unevidenced gate keeps the release `NOT_HOMOLOGATED`.

| Gate | Weight | Current | Evidence / gap |
|---|---:|---:|---|
| Canonical build + CI baseline | 15 | 13 | Windows CI exists and prior exact candidates passed; final release hash still requires complete release evidence. |
| Windows secure runtime primitives | 20 | 17 | Job Object/AppContainer/network and related fail-closed controls have native Windows evidence; filesystem read isolation is not yet fully proven for the intended restrictive authority profile. |
| End-to-end Windows runtime | 15 | 5 | Physical authenticated EngineHost exists; complete Desktop/client -> EngineHost -> worker -> embedded Python -> private Chromium path is not yet evidenced. |
| Installer + lifecycle | 10 | 0 | Clean MSI install, first run, repair, upgrade, interrupted-operation recovery, uninstall and residue checks are not yet evidenced. |
| Distribution security | 10 | 0 | Defender scan, Authenticode, signed update manifest and downgrade rejection are not yet evidenced for exact release artifacts. |
| Backend / Execution Authority integration | 10 | 4 | Security baseline exists; PostgreSQL-backed integration requires configured real test database and non-skipped execution. |
| Regression + performance + stability | 10 | 4 | Automated regression suites exist; release-candidate soak/performance/stability evidence is incomplete. |
| Minimum UX + accessibility | 5 | 1 | High-DPI/accessibility smoke and real visual regression audit remain outstanding. |
| Exact-hash release evidence package | 5 | 1 | Evidence format is defined; a complete release package for the exact candidate has not been produced. |
| **Total** | **100** | **45** | **NOT_HOMOLOGATED** |

## Mandatory Windows release gates

Status vocabulary: `PASS`, `FAIL`, `PARCIAL`, `BLOQUEADO`, `INFRASTRUCTURE_INCONCLUSIVE`, `NOT_RUN`.

1. **Exact candidate definition**
   - source commit frozen;
   - release artifact SHA-256 recorded;
   - toolchain and Windows image recorded;
   - no rebuild after final validation.

2. **Build and automated regression**
   - Release build on Windows;
   - Core tests;
   - Remote Security tests;
   - Orchestrator/Judge tests;
   - zero unexpected skips in mandatory suites;
   - source/publication policy gates.

3. **Secure Windows execution**
   - Job Object containment;
   - restricted-token/AppContainer identity where required;
   - network authority enforcement;
   - filesystem authority enforcement matching the claimed profile;
   - backend selector fail-closed behavior;
   - runtime pinning/hash checks;
   - attestation binding and execution proof;
   - hostile tests for escape, wrong token, stale/replayed authority and unsupported backend profiles.

4. **End-to-end product smoke**
   - Desktop/client launch;
   - Desktop/client -> EngineHost;
   - EngineHost -> worker;
   - embedded Python path;
   - private Chromium path;
   - Research Browser deterministic HTTPS self-test;
   - controlled authorized research fixture;
   - Evidence -> Blueprint generation and integrity validation.

5. **Installer and lifecycle**
   - clean MSI installation;
   - mandatory terms / first-run behavior;
   - repair after controlled payload corruption;
   - major upgrade from previous candidate when applicable;
   - recovery after interrupted engine/browser/update operation;
   - uninstall;
   - product-owned residue verification.

6. **Security of distributed artifact**
   - Microsoft Defender scan when available;
   - Authenticode verification for external Windows distribution;
   - signed update manifest;
   - downgrade/rollback rejection;
   - installer/application hashes match the validated evidence package.

7. **Backend integration**
   - real PostgreSQL-backed Execution Authority integration tests;
   - `AEVRIX_AUTHORITY_TEST_DATABASE_URL` configured in the controlled validation environment;
   - mandatory database tests execute without skip;
   - restart/replay/durability scenarios pass where applicable.

8. **Performance and stability**
   - startup smoke;
   - repeated mission/runtime cycles;
   - process/resource cleanup;
   - bounded CPU/memory behavior under governed policies;
   - timeout/cancellation recovery;
   - endurance/soak run with no unbounded resource growth;
   - no release-blocking crash/hang regression.

9. **Minimum UX / accessibility**
   - visual inspection on real Windows;
   - high-DPI smoke;
   - keyboard/navigation smoke;
   - minimum accessibility smoke;
   - critical-path UI has no blocking clipping, invisible controls or dead navigation.

10. **Release evidence package**
    - source commit;
    - artifact SHA-256;
    - OS image/device;
    - runner/tool versions;
    - status for every mandatory step;
    - logs;
    - screenshots where applicable;
    - generated manifests and hashes;
    - final release decision.

## Release decision rules

### `NOT_READY_FOR_GENERAL_USE`
Default state while any mandatory Windows gate is `NOT_RUN`, `PARCIAL`, `BLOQUEADO`, or `FAIL`.

### `READY_FOR_CONTROLLED_PILOT`
May be declared only when the full Windows critical path, installer lifecycle, core security gates and rollback protections pass for an exact candidate, with no open P0/P1 defect. Limited non-production gaps must be explicitly documented and must not weaken security or data integrity.

### `HOMOLOGATED`
May be declared only when **all applicable mandatory gates in `docs/VALIDATION.md` and this control plane are `PASS` for the exact hashes to be released**.

## Current highest-priority blockers

1. Prove the complete Windows product path on the exact candidate.
2. Close the intended filesystem isolation/read-boundary claim or keep the restrictive profile fail-closed.
3. Produce and validate MSI lifecycle evidence.
4. Execute real PostgreSQL-backed Execution Authority integration tests without mandatory skips.
5. Produce Defender + Authenticode + signed-update/downgrade evidence for exact artifacts.
6. Execute release-candidate performance/stability/soak and recovery testing.
7. Execute high-DPI/accessibility and real visual-regression smoke.
8. Assemble the exact-hash AVA release evidence package.

## QA operating rule

Implementation work belongs to the relevant subsystem branches/chats. This QA control plane must not convert missing implementation into a synthetic `PASS`. It observes evidence, reproduces tests where possible, records regressions, and issues the release decision.
