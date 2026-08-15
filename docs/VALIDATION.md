# AVA — AEVRIX Validation Audit

AVA is a mandatory delivery gate. A green compile is not enough.

Passing CI unit/integration tests proves only the exercised test scope; it does not satisfy installer, runtime, signing, antivirus, accessibility, emulator or real-device AVA gates.

## Windows final gate

For the **exact hashes that will be released**:

- build on real Windows;
- clean MSI installation;
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
- artifact SHA-256;
- OS/device image;
- runner/tool versions;
- step status (`PASS`, `FAIL`, `PARCIAL`, `BLOQUEADO`, `INFRASTRUCTURE_INCONCLUSIVE`);
- logs;
- screenshots where applicable;
- generated manifests and hashes.

Never rebuild after final validation and release the rebuilt artifact under the validated label.
