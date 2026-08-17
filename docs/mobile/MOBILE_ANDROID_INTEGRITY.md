# AEVRIX Mobile Android Integrity & Bundle Evidence v0.7

## Objective

Add vendor-origin Android evidence for package integrity and App Bundle structure without replacing independent analysis engines.

## `apksigner verify`

The Android SDK Build Tools `apksigner` plan is verification-only:

- `verify --verbose --print-certs` on an explicitly authorized APK;
- offline and non-mutating;
- preserves the source APK SHA-256 in the invocation plan;
- captures scheme-verification facts and signing-certificate SHA-256 values when the current output format is recognized.

Parser behavior is conservative. A successful process with an unknown future output format stays `UNMEASURED_OUTPUT_FORMAT` rather than being called verified.

## bundletool read-only dump

The Google bundletool plan accepts an authorized AAB and exposes only the read-only `dump` targets needed for reconstruction evidence:

- manifest;
- resources;
- bundle config;
- runtime-enabled SDK config.

The first integration deliberately excludes build/install/signing commands because the initial requirement is evidence acquisition, not package mutation or deployment.

## Role in evidence fusion

`apksigner` and bundletool are vendor-origin cross-checks. They do not supersede JADX, Apktool, Androguard, APKiD, MobSF, Ghidra, LIEF or dynamic observation. Disagreement is retained and benchmarked.

Both capabilities enter central Capability Governance as `CANDIDATE / UNMEASURED / hard_gates=PENDING` and must prove incremental precision/reliability before promotion.
