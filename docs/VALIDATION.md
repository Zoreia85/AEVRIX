# AVA — AEVRIX Validation Audit

AVA is a mandatory delivery gate. A green compile is not enough.

Passing CI unit/integration tests proves only the exercised test scope; it does not satisfy installer, runtime, signing, antivirus, accessibility, emulator or real-device AVA gates.

## Windows final gate

For the **exact hashes that will be released**:

- build on real Windows;
- clean installation using the explicitly approved Windows installation package for the candidate (MSI or another package format approved by the product/packaging architecture); the exact installer hash must be recorded;
- mandatory terms/first-run behavior;
- application launch and visual inspection;
- backend connection and fail-closed offline behavior;
- Desktop -> EngineHost -> worker -> embedded Python -> private Chromium path;
- Research Browser deterministic HTTPS self-test;
- controlled authorized research fixture;
- Evidence -> Blueprint generation and integrity validation;
- repair after controlled payload corruption;
- major upgrade from previous candidate when applicable;
- uninstall and product-owned residue verification;
- recovery from interrupted engine/browser/update operation;
- high-DPI/accessibility smoke;
- Microsoft Defender scan when available in the validation environment;
- Authenticode verification for external Windows distribution;
- signed update manifest / downgrade rejection;
- regression suite.

The package format itself does not waive any AVA requirement. A successful package build is not an installer lifecycle PASS; install, recovery, repair, upgrade, uninstall, residue and all other applicable release gates must still be exercised against the exact releasable hashes.

## Android gate

Before an APK is called homologated:

- reproducible build;
- signed sideloadable APK;
- clean emulator/device installation;
- mandatory terms gate;
- first launch;
- navigation screenshots;
- Android Keystore device identity;
- online/offline fail-closed behavior;
- project synchronization with the same backend contracts;
- upgrade/uninstall/reinstall;
- regression tests.

## Evidence format

Every AVA run should record:

- source commit;
- canonical parent/lineage when privacy-safe canonicalization rewrites commit metadata;
- artifact SHA-256;
- OS/device image;
- runner/tool versions;
- step status (`PASS`, `FAIL`, `PARCIAL`, `BLOQUEADO`, `INFRASTRUCTURE_INCONCLUSIVE`);
- logs;
- screenshots where applicable;
- generated manifests and hashes.

Never rebuild after final validation and release the rebuilt artifact under the validated label.
