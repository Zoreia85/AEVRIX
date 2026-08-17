# Trusted Project Memory Admission

Status: architectural candidate; not canonical until promoted by the privacy-safe workflow after all applicable gates pass.

## Purpose

AEVRIX must distinguish persistence from epistemic authority. A repository may store candidate knowledge and validation records, but storage access alone must never be sufficient to create `KnowledgeTrustState.Trusted`.

The canonical authority chain is:

`EvidenceObservation -> governed execution -> ExecutionProofLedger -> external monotonic proof-head anchor -> independent Judge validation -> memory admission authorization -> encrypted project knowledge repository`

Every boundary fails closed.

## Authority separation

### Specialists, models and QIR

May propose observations, candidate statements and planning hints. They have no authority to mint Trusted memory.

### Judge

Owns the transition decision. Independent validation may produce `Validated` or `Rejected` without execution-proof admission. A validation that satisfies every Trusted criterion does **not** become Trusted automatically.

### Memory admission gate

The gate verifies exact evidence-set closure against a cryptographically valid execution-proof snapshot and requires the snapshot head to equal the head held by `IExecutionProofHeadAnchor` in the independent rollback domain.

Only after these checks may the gate mint a `TrustedKnowledgeAdmissionAuthorization`.

### Project knowledge repository

The repository is an adapter and remains replaceable. Its generic validation-outcome method accepts only `Validated` or `Rejected`. Trusted state requires the opaque authorization object. The authorization has no public constructor, so ordinary callers cannot mint it.

The repository must independently re-check authorization/candidate/validation project and target bindings before committing Trusted state.

## Required Trusted invariants

A Trusted admission requires all of the following:

1. candidate is still in `Candidate` state;
2. validation belongs to the exact candidate;
3. validation is independently Trusted-eligible;
4. validated EvidenceIds equal the candidate EvidenceIds exactly, not merely a subset;
5. observations match those EvidenceIds one-to-one;
6. every observation belongs to the same project and target;
7. PersonalData and raw secret material are excluded from Trusted project memory;
8. the supplied ExecutionProofLedger snapshot verifies cryptographically;
9. the snapshot head equals the external monotonic head anchor for the project;
10. each EvidenceId resolves through `MissionExecutionProofIdentity` to exactly one terminal `Completed` proof;
11. the terminal proof is `Succeeded` and matches mission/run, project, specialist and governed capability class;
12. the admission authorization is minted only after all previous checks;
13. the repository accepts Trusted only with that authorization and revalidates scope and validation state.

## Threats explicitly rejected

- a high-confidence model writing directly to Trusted memory;
- a Specialist or QIR component calling a generic repository promotion method;
- a caller presenting a partial independent-validation evidence set;
- cross-project or cross-target reuse of observations;
- PII or raw-secret promotion into reusable Trusted knowledge;
- a self-consistent but fabricated execution ledger whose caller also supplies the matching fake head;
- replay of a stale/rolled-back ledger head;
- bypassing the Judge by constructing a Trusted authorization directly.

## Relationship to Blueprint provenance

Trusted memory admission happens before Blueprint projection and therefore does not depend on a Blueprint. Blueprint provenance remains a later independent gate. The two stages share execution-proof identity concepts but have different authority purposes:

- Memory admission answers: **may this knowledge become reusable Trusted project memory?**
- Blueprint provenance answers: **can this reconstruction artifact prove exactly which governed executions produced its evidence?**

Neither stage may use the other as self-confirming evidence.

## QIR and quantum boundary

QIR may consume sanitized outcomes for planning utility after Judge/admission decisions, but cannot mint or raise trust state. Quantum/hybrid solvers, if ever benchmark-qualified, remain optimization adapters only and never participate in memory authority, evidence confidence or access control.

## Promotion rule

This architecture is not considered complete until Source Policy, Windows Core, Remote Security and Orchestrator Judge pass on the exact candidate and the same technical tree is promoted through the privacy-safe bot/noreply path. A branch-only green result is evidence, not canonical completion.
