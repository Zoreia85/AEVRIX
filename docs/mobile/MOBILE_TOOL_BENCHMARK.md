# AEVRIX Mobile Tool Invocation & Benchmark v0.4

## Objective

This layer turns Mobile Lab tool integrations into measurable capabilities. It does **not** create a second process runner, sandbox, security authority, or capability lifecycle. It produces validated invocation plans and benchmark evidence for the existing central AEVRIX execution and capability-governance layers.

## Governed invocation plans

Every plan is data, not direct execution. A plan contains:

- capability ID;
- exact argument vector (`argv`) with no shell interpolation;
- authorized input SHA-256 when an artifact is involved;
- confined output path when the tool writes derived evidence;
- network mode;
- explicit target-mutation flag;
- evidence kind;
- authorization state;
- deterministic command SHA-256.

Artifact plans require an explicit authorization flag and valid SHA-256. Output paths are resolved beneath a benchmark workspace and cannot escape it.

### Android SDK `apkanalyzer`

Read-only plans cover APK summary, manifest XML, permissions, DEX inventory and file inventory. These map to the official Android SDK CLI surface and are intended as a vendor-origin cross-check against third-party parsers.

### JADX

The plan decompiles Android artifacts into a confined evidence directory. It remains offline and non-mutating with respect to the source artifact. Decompiler output is evidence, not reconstructed source truth.

### Apktool

The plan decodes APK resources/smali into a confined evidence directory. It is complementary to JADX and `apkanalyzer`; benchmark disagreement is retained rather than silently reconciled.

### Androguard

Plans currently expose manifest/AXML and certificate-signature inspection. This creates a Python-native parser cross-check without promoting it over other engines.

### ADB observation

The first ADB plans are deliberately observation-only and target an already-created disposable environment:

- device inventory;
- device properties;
- snapshot logcat;
- screenshot capture;
- current activity-state dump.

The Mobile Lab does not use these plans to alter authentication, security controls, application integrity, or a non-disposable user device.

## Canonical benchmark evidence

`BenchmarkEvaluator` compares tool outputs using canonical `FindingKey(category, subject, detail)` records.

For each capability it measures only facts supported by the corpus:

- attempts and successful runs;
- reliability;
- median successful runtime;
- observed finding count;
- truth-case count;
- true positives, false positives and false negatives when ground truth is known;
- recall, precision and F1 when ground truth is known;
- signals observed only by that tool relative to peers;
- per-case disagreements between tools.

When a benchmark case has no known truth, accuracy remains `UNMEASURED`. A unique signal is **not** automatically a correct signal; it becomes a target for independent validation.

The benchmark never emits `ADMITTED`, `PREFERRED`, or any aggregate lifecycle score. Those decisions remain with central Capability Governance, which can combine these measurements with security, auditability, necessity, cost-benefit, maintainability and other governed dimensions.

## Initial benchmark matrix

The first Android corpus should run the same authorized APK cases through:

1. Android SDK `apkanalyzer`;
2. JADX;
3. Apktool;
4. Androguard;
5. local MobSF only after its local-service adapter passes its own containment gate.

Static structural findings should be normalized into the same `FindingKey` vocabulary. Cases with known synthetic fixtures can measure precision/recall; real authorized applications without exhaustive truth remain useful for disagreement discovery but cannot produce synthetic accuracy percentages.

Dynamic evidence follows separately through the disposable Android adapter and ADB observation plans. Frida remains an escalation-only observation capability for gaps that ordinary static/dynamic evidence cannot explain.

## Promotion rule

No tool is promoted because it installed successfully, has many GitHub stars, produces more output, or uniquely reports an item. Promotion requires repeatable measured benefit under the existing AEVRIX capability policy and passing hard gates.
