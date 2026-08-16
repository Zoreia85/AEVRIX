# AEVRIX — Master Roadmap

Status: CANONICAL MASTER ROADMAP

This roadmap is architectural. It tracks major system capabilities and trust boundaries, not small implementation tasks.

## Phase A — Canonical Brain

Goal: one governed intelligence model shared by all product surfaces.

Exit conditions:

- Mission Director is the only canonical mission composition path;
- specialist execution is proof-bound;
- Judge is a separate admissibility boundary;
- evidence classes and confidence semantics are explicit;
- QIR cannot bypass Judge or provenance;
- trusted memory cannot be written directly by providers/specialists.

## Phase B — Provenance Closure

Goal: every reconstructible output is traceable to governed execution.

Exit conditions:

- Artifact -> Execution -> Proof Ledger -> Evidence -> Judge -> Blueprint is closed;
- Blueprint admission rejects missing or cross-scope provenance;
- export/promotion cannot bypass proof-bound knowledge;
- tampering and replay attacks are covered by hostile tests.

## Phase C — Memory Architecture

Goal: useful learning without data leakage.

Exit conditions:

- execution, project, candidate, trusted and cross-run memory layers are distinct;
- every memory write has an explicit admission policy;
- project/user/workspace boundaries are enforced;
- cross-run learning uses privacy-safe derived representations only;
- rollback/replay semantics are defined.

## Phase D — Capability and Adapter Fabric

Goal: make external technology powerful without making it trusted by default.

Exit conditions:

- adapters expose a common capability contract;
- source/revision/digest/license/security approval are bound to runtime admission;
- MCP, models, local runtimes and future providers use the same governance model;
- network/data exposure limits are enforced and attestable;
- adapter routing is deterministic or explicitly resolves ambiguity.

## Phase E — Isolation and Trust Domains

Goal: strong separation between users, projects, executions and authorities.

Exit conditions:

- user + workspace + execution storage boundaries are physically enforced;
- sensitive local artifacts use workspace-bound encryption where required;
- EngineHost remains an isolated process boundary;
- Execution Authority remains an independent trust domain;
- promotion claims are durable and replay-safe;
- no component claims stronger isolation than the host actually enforces.

## Phase F — Windows Product Architecture

Goal: make Windows the first complete AEVRIX product surface.

Exit conditions:

- Desktop supervises EngineHost lifecycle;
- complete project -> analysis -> evidence -> Blueprint workflow is operational;
- contextual help is canonical and complete;
- installer/repair/upgrade/uninstall architecture is reproducible;
- signed update/downgrade policy is defined;
- clean-machine validation and release evidence exist.

## Phase G — Developer API / SDK

Goal: expose the same brain without creating a second product logic.

Exit conditions:

- versioned public API contract;
- authentication, authorization and tenancy match core policy;
- asynchronous jobs and bounded uploads;
- Evidence and Blueprint retrieval preserve provenance;
- quotas/rate limits/audit semantics are explicit;
- official SDKs are generated or validated from the canonical API schema.

## Phase H — Mobile Console

Goal: secure monitoring and interaction without duplicating the heavy engine.

Exit conditions:

- secure authentication and device binding;
- project/job/evidence/Blueprint views;
- approval flows for protected actions;
- local storage minimization;
- signed Android release evidence;
- iOS architecture follows the same trust model.

## Phase I — Release and Homologation Architecture

Goal: transform validated components into trustworthy delivered artifacts.

Exit conditions:

- exact-artifact release model;
- code signing and update signing;
- install/repair/upgrade/uninstall/recovery evidence;
- security and privacy audit package;
- accessibility/DPI/usability validation;
- reproducible release manifests and hashes;
- no HOMOLOGATED label before all applicable evidence exists.

## Phase J — Advanced Optimization / Quantum Research

Goal: improve scheduling or combinatorial optimization only when measurable.

Exit conditions for any production quantum/hybrid path:

- real AEVRIX workload benchmark;
- strongest classical baseline documented;
- repeatable quality improvement or operational advantage;
- latency and cost included in comparison;
- fallback to classical path;
- quantum adapter remains non-authoritative for correctness or evidence admission.

## Dependency graph

```text
Canonical Brain
   |
   +--> Provenance Closure
   |       |
   |       +--> Memory Architecture
   |       +--> Windows Product
   |       +--> API / SDK
   |       +--> Mobile Console
   |
   +--> Capability / Adapter Fabric
   |       |
   |       +--> Isolation / Trust Domains
   |
   +--> QIR / Advanced Optimization

Windows + API + Mobile + Trust Domains
   |
   v
Release / Homologation Architecture
```

## Governing rule

The roadmap moves by verified architectural capability. Local bug fixes, CI repairs and isolated refactors may be necessary implementation work, but they do not independently advance architectural phase completion unless they close an explicit exit condition above.
