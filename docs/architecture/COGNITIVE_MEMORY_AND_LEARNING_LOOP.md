# AEVRIX Cognitive Memory and Learning Loop

Status: architectural decision, not a runtime-completeness claim.

## Purpose

AEVRIX must learn from prior work without converting hypotheses, model output, user preference, or repeated inference into fact. This document defines the canonical memory topology and the only permitted feedback path between Evidence, Judge, QIR, Memory and Orchestrator.

The governing rule is:

> Execution may create observations. The Judge may create trust. Memory may preserve trusted knowledge. QIR may learn utility. None of these components may manufacture truth for another.

## Canonical cognitive loop

```text
Mission
  -> Orchestrator
  -> Specialists / governed capabilities
  -> Execution Proof + Evidence
  -> Evidence Fusion
  -> Judge
       -> Candidate / Validated / Trusted / Rejected
       -> contradiction state
       -> confidence and provenance
  -> Project Memory Admission Gate
  -> Project Knowledge Vault
  -> Optional Global Learning Sanitization Gate
  -> Global Pattern Memory
  -> QIR utility learning
  -> Orchestrator planning hints
  -> next Mission
```

There is intentionally no direct `specialist -> trusted memory`, `LLM -> trusted memory`, `QIR -> evidence`, or `memory -> Judge verdict` path.

## Memory classes

AEVRIX uses four logically separate memory classes.

### 1. Working Memory

Ephemeral mission-local state used by the Orchestrator and specialists. It may contain hypotheses, partial plans and unresolved observations. It is destroyed or compacted when the mission ends and is never treated as evidence merely because it was retained during execution.

### 2. Project Evidence Memory

Project-scoped immutable or append-only evidence references and execution provenance. It is bound to project, target, workspace, subject and execution context. Evidence remains evidence; storage does not increase its trust level.

### 3. Project Knowledge Memory

Judge-admitted knowledge retained in the encrypted project vault. Records preserve their source evidence, validation record, confidence, contradiction state, sensitivity, originating mission and proof/ledger references.

Only `Validated` or `Trusted` knowledge may enter this memory. `Candidate`, `Contested`, `Insufficient` and `Rejected` states are not admitted as reusable project knowledge, although their audit records may remain available for traceability.

### 4. Global Pattern Memory

Cross-project learning contains only sanitized, non-identifying abstractions that have passed explicit global-learning eligibility. It stores reusable patterns such as capability usefulness, task decomposition patterns, failure modes, format/language affinities and evidence-yield statistics.

It must not contain raw project artifacts, secrets, personal data, customer identifiers, source paths, private endpoints, browser/session material, or reconstructable project-specific content.

## Judge authority

The Judge is the exclusive authority for epistemic promotion. It may consult multiple validators and evidence-fusion results, but it must independently re-derive the admissibility state from authoritative records.

The Judge cannot use QIR scores, model confidence, specialist reputation, prior memory frequency, or user preference as substitutes for evidence.

Repeated claims do not become convergent evidence unless they are independently grounded. Ten agents repeating one source are one evidentiary lineage, not ten corroborating sources.

## Memory admission gate

Before project knowledge is persisted as reusable knowledge, the admission gate must verify:

1. exact project and target binding;
2. authoritative Judge state is `Validated` or `Trusted`;
3. cited evidence exists and is admissible;
4. evidence provenance closes to governed execution proof;
5. no unresolved contradiction blocks promotion;
6. sensitivity permits the destination memory class;
7. PII/secret-bearing content is rejected or sanitized according to policy;
8. the record has not been rebound to a different claim, project, target or evidence set;
9. the knowledge revision is monotonic or explicitly superseding, never silent overwrite.

Failing any check means no admission.

## Contradiction model

AEVRIX must preserve contradiction rather than average it away.

A knowledge record can be superseded only by a later Judge decision referencing both the previous record and the new evidence basis. The prior record remains auditable and receives a terminal state such as `Superseded` or `Revoked`.

A contradiction automatically prevents global learning from the affected claim until resolved.

## QIR learning boundary

QIR is a planning and utility-learning system, not a truth engine.

QIR may learn from outcomes such as:

- which specialist or adapter produced admissible evidence for a task class;
- latency, cost and failure rate;
- evidence yield per capability;
- successful task decomposition patterns;
- sandbox/runtime compatibility;
- retry value and diminishing returns;
- Judge acceptance/rejection as an outcome signal.

QIR must not learn that a proposition is true merely because the Judge accepted a previous proposition with similar wording.

QIR output is a recommendation with bounded influence over scheduling. The Orchestrator retains policy authority and must remain able to choose a different plan when constraints or evidence requirements demand it.

## Anti-self-confirmation rules

The following feedback loops are forbidden:

- memory-derived text being re-ingested as independent evidence for the same claim;
- a Blueprint being treated as proof of the evidence that produced it;
- QIR preference increasing Judge confidence;
- model consensus without independent evidence increasing evidentiary lineage count;
- prior `Trusted` knowledge from another project entering a new project as project fact without fresh admissibility;
- retrying the same deterministic analysis and counting identical output as corroboration.

Memory can suggest where to look. Only new admissible evidence can strengthen a claim.

## Global learning sanitization

Cross-project learning requires an explicit sanitizer/eligibility adapter. Its output must be a new abstraction object with its own provenance, not a copy of the source knowledge record.

At minimum it must remove or reject:

- raw artifact content;
- identifiers for project, user, organization, target or customer;
- local paths and machine identifiers;
- domains/endpoints not explicitly classified as public and reusable;
- credentials, tokens, cookies, private keys and secret material;
- personal data;
- proprietary reconstructed implementation details that could reveal one customer's system.

The sanitized abstraction retains only enough provenance to prove that it came from Judge-admitted knowledge without exposing the original project.

## Forgetting, decay and invalidation

AEVRIX memory is not append-forever truth.

- Working memory expires with mission lifecycle.
- Evidence retention follows project policy and legal/privacy requirements.
- Project knowledge can be superseded or revoked but not silently rewritten.
- Global pattern memory carries observation counts, recency and version compatibility; stale patterns lose planning weight.
- A capability/runtime version change may invalidate prior performance priors without invalidating historical evidence.
- Security or privacy revocation can immediately make a memory item ineligible for use even if it remains auditable.

## Retrieval rules

Retrieval is scoped before ranking.

Project memory retrieval must first bind subject, workspace, project and target as applicable. Semantic similarity is never allowed to cross an isolation boundary.

Global pattern retrieval returns sanitized planning abstractions only. It cannot return project knowledge payloads.

## Deterministic provenance closure

Every reusable project-knowledge record must be able to resolve the following chain:

```text
KnowledgeRecord
  -> JudgeDecision
  -> EvidenceSet
  -> Evidence item(s)
  -> governed specialist execution(s)
  -> ExecutionProofLedger head / record(s)
  -> project + mission + target scope
```

If any link cannot be verified, the record is unusable for Blueprint promotion and cannot seed global learning.

## Security properties

Memory adapters are ports, never trusted by location alone. Local files, SQL databases, vector indexes and remote stores must satisfy the same contracts.

Vector/semantic indexes are treated as derived caches. They are not authoritative knowledge stores and may be rebuilt from admitted records. A vector match must resolve back to an authoritative record before use.

No memory backend receives raw encryption keys through logs, telemetry or serialized records.

## Quantum/hybrid boundary

Memory admission, provenance verification and Judge decisions remain deterministic classical security operations.

Quantum/hybrid backends may be evaluated only for bounded optimization subproblems such as large mission scheduling or search-space ordering. They never receive authority over evidence truth, trust promotion, access control or memory admission.

A quantum adapter remains experimental until a reproducible benchmark beats the best approved classical baseline on solution quality and at least one material operational dimension such as end-to-end latency or cost without unacceptable reliability loss.

## Implementation sequence

The architectural implementation order is:

1. canonical memory record contracts and explicit memory-class types;
2. project-memory admission gate bound to Judge + execution provenance;
3. contradiction/supersession state machine;
4. global-learning sanitizer and eligibility contract;
5. global pattern store with no project-identifying payloads;
6. QIR utility-learning contract that consumes outcomes, not truth claims;
7. Orchestrator planning-hint interface with bounded influence;
8. hostile tests for cross-project retrieval, self-confirmation, stale memory and forged provenance;
9. optional benchmark harness for optimization backends.

## Non-goals

This architecture does not define UI, a specific vector database, a specific LLM provider, or a requirement for cloud storage. It defines the cognitive trust boundaries every implementation must preserve.
