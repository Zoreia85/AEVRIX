# AEVRIX — Public Sanitation Status

Authority: `Zoreia85/AEVRIX#494`  
Mode: temporary PUBLIC operating mode while private GitHub Actions capacity is unavailable.  
Black Core: PRIVATE/FROZEN; no private-only source may be copied into this repository.

## Canonical branch semantics

- `main`: INTEGRATION_APPROVED only; never implies RELEASE_HOMOLOGATED.
- `quarantine/<stream>/<topic>`: useful but unapproved candidate; may fail or lack evidence.
- `rc/<version>`: exact Windows test candidate assembled only from integration-approved main.
- historical branches: must be classified before eventual deletion.

## Current sanitation snapshot — 2026-08-17 BRT

Observed branch count: 251 including `main`.

Observed open PRs: 4 before first closure pass.

### Open PR classification

| PR | Current classification | Reason |
|---|---|---|
| #496 S3 Windows installed Desktop startup diagnostics | QUARANTINE / KEEP_ACTIVE | Directly targets known installed-Desktop first-run/startup blocker. Disclosure-safe Windows diagnostics and exact-head CI. Must not merge until exact public Windows CI and S4 evidence are green. |
| #493 Android integrity / AAB evidence | QUARANTINE / MERGE_CANDIDATE | Unique Mobile Lab evidence code exists ahead of main. Branch is stale/diverged and capability registry delta is risky; rebase/reconstruct on current main before any promotion. |
| #479 Mobile artifact admission gate | QUARANTINE / MERGE_CANDIDATE | Unique fail-closed admission code exists ahead of main. Reconstruct on current main and run current policy/security/regression gates before promotion. |
| #364 AI provider budget gate | BLOCKED_UNTIL_PRIVATE / ARCHIVE_EVIDENCE | Unique remote-brain budgeting logic exists, but this is strategic core/provider-accounting logic and is outside the temporary public-safe expansion boundary. Preserve branch/provenance; close PR without merge while public mode is active. |

### Historical branch families

The following families are presumptive cleanup candidates and are not authorities merely because the ref exists:

- `tmp/*`: TRASH_CANDIDATE after reference/equivalence scan.
- stale `validation/*`: ARCHIVE_EVIDENCE/TRASH_CANDIDATE after exact evidence is retained.
- superseded `diagnostic/*`: ARCHIVE_EVIDENCE or TRASH_CANDIDATE after outcome is recorded.
- repeated `*-v1`, `*-v2`, `*-v3`, `*-replay`, `*-canonical`, `*-clean-*`: compare newest surviving implementation; older copies become TRASH_CANDIDATE only after equivalence/reference scan.
- private-bound/crown-jewel evolution: BLOCKED_UNTIL_PRIVATE; do not extend in public.

## Promotion gate

A candidate may enter `main` only with:

1. one owning stream;
2. exact base SHA and head SHA;
3. affected files/contracts inventory;
4. tests actually executed on the exact candidate;
5. policy/security results;
6. regression/compatibility result;
7. unresolved skips/blockers explicitly recorded;
8. independent S4 verdict PASS.

Any required FAIL, stale evidence, missing mandatory evidence, mandatory SKIP, unknown provenance or IP-boundary violation => QUARANTINE.

## Administrative main-write incident

Commits `9c9f4c950dbda456741b5a2b3130f54aa97c1ddb`, `b33bb50a0583e46b7a616cd4d7149b45c8d129cc`, `5bfd4ace874cb582981230ed30efb02bc6964198`, and `8aa0548ff96a930ee8dae1e586be0340a10ce011` created/updated this sanitation ledger directly on `main` through the connected Contents API. These are administrative documentation-only writes, not functional implementation promotions. The resulting main SHA must be post-validated by public CI/policy checks before it is called fully trusted. No functional code may use this direct-main path.

## Deletion limitation

The connected GitHub interface currently exposes branch inspection/ref movement but not branch-ref deletion. Therefore branch refs cannot be physically purged from this session. Sanitation work must first classify and close obsolete PR authority, preserve hashes/evidence, and prepare a deletion ledger. Physical branch deletion can then be performed when a deletion-capable GitHub interface is available. This limitation must never be reported as completed deletion.

## Exit criteria

Sanitation is complete only when:

- one operational authority is active;
- every open PR has a current owner/verdict;
- every surviving non-main branch is KEEP_ACTIVE/QUARANTINE/ARCHIVE_EVIDENCE with a reason;
- obsolete branch refs have been physically deleted where safe;
- `main` contains only integration-approved code;
- no public source violates the Black Core/IP boundary;
- exact public CI gates are green for the canonical main SHA;
- Internal Canary Windows can be generated from an exact `rc/*` candidate.
