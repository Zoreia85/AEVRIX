# External AVA Evidence Contract

Some release gates require real Windows/device actions that cannot be credibly proven by a hosted unit-test run. Those results enter the strict homologation evaluator through `docs/qa/evidence/release-gates.json` only after an independent AVA execution.

The file is optional during ordinary development. Its absence means the corresponding gates remain `NOT_RUN`.

## Required root fields

```json
{
  "schemaVersion": 1,
  "sourceCommit": "<exact 40-character candidate commit>",
  "gates": {}
}
```

`sourceCommit` must exactly match the commit checked out by the Windows readiness workflow. Mismatched evidence is rejected.

## Gates allowed from external evidence

Only these gates may be supplied manually/external to the automated probe:

- `windows-e2e-runtime`
- `installer-lifecycle`
- `distribution-security`
- `execution-authority-db`
- `performance-stability`
- `ux-accessibility`

Automatic gates such as EngineHost build, Core tests, Remote Security, Orchestrator/Judge, and physical Desktop/WinUI detection cannot be overridden by external JSON.

## PASS requirements

An external gate may be `PASS` only when it includes:

- `status: "PASS"`;
- `evidenceRef`: immutable or durably auditable identifier for the AVA record/package;
- `evidenceSha256`: SHA-256 of the normalized evidence package/record;
- `artifactSha256` when the gate validates a distributed binary/artifact.

Example shape:

```json
{
  "schemaVersion": 1,
  "sourceCommit": "0123456789abcdef0123456789abcdef01234567",
  "gates": {
    "installer-lifecycle": {
      "status": "PASS",
      "evidenceRef": "ava:windows-installer-run-001",
      "evidenceSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "artifactSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
    }
  }
}
```

The example hashes above are placeholders and are not release evidence.

## Minimum content by gate

### windows-e2e-runtime

Evidence package must identify and prove the same exact candidate through Desktop/client -> authenticated EngineHost -> worker -> embedded Python -> private Chromium, Research Browser deterministic HTTPS self-test, controlled authorized fixture, and Evidence -> Blueprint integrity.

### installer-lifecycle

Evidence package must include clean MSI install, mandatory first-run/terms behavior, repair after controlled corruption, applicable major upgrade, interruption recovery, uninstall, and product-owned residue check.

### distribution-security

Evidence package must include exact artifact hashes, Microsoft Defender result when available, Authenticode verification for external distribution, signed update manifest, and downgrade/rollback rejection.

### execution-authority-db

Evidence must show PostgreSQL-backed Execution Authority integration on the exact candidate with mandatory DB tests executed rather than skipped, including restart/replay/durability scenarios where applicable.

### performance-stability

Evidence must record workload/cycle count, elapsed time, crashes/hangs, peak and end resource observations, cleanup behavior, cancellation/timeout recovery, and soak/endurance conclusion.

### ux-accessibility

Evidence must include real Windows visual inspection plus high-DPI, keyboard/navigation, minimum accessibility, and critical-path clipping/dead-navigation checks.

## Prohibition on synthetic PASS

Changing a JSON status to `PASS` without the corresponding immutable evidence does not satisfy AVA and is a governance violation. The validator rejects malformed PASS records, but cryptographic formatting alone is not proof that an execution occurred; the durable evidence package remains mandatory.
