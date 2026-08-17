# AEVRIX Mobile Native/Binary Toolchain v0.6

## Why this layer exists

APK and IPA reconstruction cannot stop at Java/Kotlin/Swift/Objective-C surfaces. Native ELF/Mach-O libraries, JNI bridges, custom protectors, compiled engines and obfuscation layers can contain behavior that ordinary resource/decompiler tools do not explain.

This layer adds complementary candidates without promoting any of them by reputation.

## Ghidra headless

Ghidra is used only through a local headless plan for authorized derived ELF/Mach-O/native-library evidence. The plan:

- imports a SHA-256-linked derived artifact into an ephemeral project;
- enables read-only processing;
- deletes the created project when analysis closes;
- bounds per-file analysis time and CPU parallelism;
- runs offline;
- never mutates the source evidence.

Ghidra project output is analytical evidence. Decompiled pseudocode is not treated as original source truth.

## APKiD

APKiD is used as a narrow Android fingerprinting signal for compilers, packers, protectors, obfuscators and related shielding/RASP indicators. The plan accepts an authorized APK or a SHA-linked derived DEX, requests JSON output, runs offline and does not mutate the target.

A detection is a clue for analysis strategy; it is not proof of maliciousness and is not a reason to bypass the protection.

## LIEF

LIEF is registered as a local-library candidate for cross-format parsing of ELF, Mach-O and Android binary formats. Initial probing reads Python distribution metadata without importing candidate code. Deeper parser adapters must benchmark structural accuracy against vendor/native tools before promotion.

## Appium

Appium is deliberately **comparison-only** at this stage. Its cross-platform WebDriver model can be valuable for Android/iOS/hybrid observation, but AEVRIX already has native Android SDK/UIAutomator and Apple simulator paths. Appium must therefore prove incremental state/flow coverage, reliability or maintenance advantage before it is admitted; otherwise it remains redundant.

## Evidence lineage

A native/derived artifact record carries both its own SHA-256 and the SHA-256 of the authorized source mobile package. This keeps ELF/Mach-O/DEX findings attributable to the exact APK/IPA evidence from which they were derived.

## Governance

Ghidra, APKiD, LIEF and Appium enter as `CANDIDATE / UNMEASURED / hard_gates=PENDING`. Installation, popularity, maturity or output volume do not constitute effectiveness. Promotion requires measured incremental precision, reliability, security controllability, performance and cost-benefit through central AEVRIX Capability Governance.
