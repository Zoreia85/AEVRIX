# AEVRIX Brain Control Plane

Status: canonical architecture decision.

## Objective

The AEVRIX brain is a governed control plane that coordinates specialized analysis without concentrating every capability or trust decision inside one model or process.

The brain owns planning, arbitration and learning boundaries. Capability adapters own execution. The Judge owns epistemic promotion. Execution Authority owns irreversible promotion authority. Memory stores admitted state. QIR optimizes future planning but does not decide truth.

## Authority map

| Component | May decide | Must not decide |
|---|---|---|
| Mission Director / Orchestrator | task graph, sequencing, concurrency, specialist selection, retry/stop | whether evidence is true, whether a promotion is authorized |
| Capability Registry / Fabric | adapter eligibility and compatibility | mission truth or Judge confidence |
| Specialist | observations and candidate interpretations within granted scope | trusted knowledge, global memory admission |
| Evidence Bus / Fusion | normalized evidence lineage and convergence calculation | policy bypass or promotion authority |
| Judge | candidate/validated/trusted/rejected knowledge state | runtime access, credential scope, release promotion |
| Project Memory Admission | whether Judge-approved knowledge may persist as reusable memory | independent truth reassessment |
| QIR | utility priors and planning hints | evidence status, access control, Judge verdict |
| Execution Authority | authorization for irreversible promotion | technical evidence generation |
| Blueprint Gate | whether admitted knowledge can become reconstructable output | creation of new factual claims |

No adapter, model provider or external service may combine two authorities merely for convenience.

## Mission lifecycle

1. A mission is created with explicit project, workspace, subject, target, authorization class and objective.
2. The Orchestrator decomposes it into capability-neutral tasks.
3. Policy and Capability Fabric determine which adapters are eligible.
4. The scheduler applies classical planning plus optional QIR utility hints.
5. Each execution receives a proof-bound identity and least-authority runtime scope.
6. Specialists emit observations/evidence, not trusted facts.
7. Evidence Fusion groups independent lineages and exposes contradiction/convergence state.
8. The Judge produces a knowledge decision bound to the authoritative evidence set.
9. Admitted knowledge may enter project memory.
10. Blueprint/reconstruction can consume only admissible knowledge with provenance closure.
11. Sanitized outcome metadata may update global pattern memory and QIR.

## Orchestrator design

The Orchestrator is deterministic around policy and nondeterministic only where optimization is permitted.

Its plan is an explicit object with:

- mission and project identity;
- task graph and dependencies;
- required evidence classes;
- eligible capability classes, not hard-coded vendors;
- maximum concurrency and resource budgets;
- network/filesystem/runtime authority per task;
- stop conditions;
- fallback chain;
- QIR hint set and the reason each hint was accepted or ignored.

The Orchestrator must remain functional with QIR disabled. QIR is an accelerator, never a single point of correctness.

## Specialist model

A specialist is defined by a contract rather than by a specific AI model or tool. Specialists may be classical algorithms, local models, remote models, OCR engines, parsers, dynamic-analysis adapters, network observers, human-approved tools or future hybrid optimizers.

All specialists return structured results with confidence, limitations, source lineage and bounded output size. Unstructured model prose can be attached as explanatory material but cannot bypass structured evidence contracts.

## Council and disagreement

AEVRIX may run multiple specialists on the same task when the expected information gain justifies the cost.

Consensus is not a voting mechanism for truth. The Council produces:

- independent hypotheses;
- evidence references;
- contradiction markers;
- requested discriminating experiments;
- uncertainty.

The Judge resolves only after evidence requirements are satisfied. If evidence remains contradictory, `Contested` is a valid terminal result and must not be converted into a synthetic average.

## Judge architecture

The Judge is logically independent from the specialists whose output it evaluates. A deployment may use model assistance for explanation or classification, but authoritative promotion logic remains constrained by deterministic policy, evidence lineage and validation state.

Judge decisions are append-only revisions. A later decision may supersede an earlier one but must reference it and explain the evidence delta.

## QIR architecture

QIR maintains utility knowledge about execution strategy. Its state is separated from project factual memory.

A QIR observation can include:

- task class;
- capability class and version;
- environment/runtime class;
- latency and cost;
- completion/failure category;
- evidence yield;
- Judge acceptance outcome;
- retry value;
- resource consumption.

It must not include raw sensitive project content in global learning.

QIR emits ranked planning hints with confidence and provenance. The Orchestrator records whether the hint was followed and the observed outcome, allowing online evaluation without ceding authority.

## Exploration versus exploitation

The scheduler should support bounded exploration so the system does not permanently lock onto an early mediocre adapter. Exploration is policy-limited by cost, security, authorization and mission criticality.

For high-risk or irreversible missions, exploitation of proven capabilities may be required and experimental adapters can be excluded entirely.

## Provider neutrality

External AI/model providers are adapters behind capability contracts. No provider is the AEVRIX brain.

Replacing Ollama, a cloud model, an OCR engine, a browser runtime or a code agent must not require changing Mission, Judge, Memory or Blueprint contracts.

Provider metadata includes approval, license/provenance where applicable, pinned version/revision, privacy class, supported data sensitivity, network requirements, health status and cost model.

## Failure model

The control plane is fail-closed around authority and fail-soft around optional intelligence.

Examples:

- QIR unavailable -> run policy-compliant classical plan;
- preferred specialist unavailable -> select approved fallback or return insufficient capability;
- memory index unavailable -> use authoritative store or disable memory-assisted retrieval;
- Judge unavailable -> evidence may persist, but no trusted promotion occurs;
- Execution Authority unavailable -> no irreversible promotion occurs;
- remote model unavailable -> local/classical fallback only if policy and evidence requirements permit it.

## Observability

Operational telemetry is distinct from evidence. Logs and metrics may describe duration, state transitions and failures, but are not automatically admissible evidence.

Every mission exposes a user-readable state machine:

`Planned -> Authorized -> Executing -> Evaluating -> Judged -> Admitted/Blocked -> BlueprintEligible/NotEligible`.

The Desktop, API and Mobile surfaces consume the same canonical state definitions.

## Architectural invariants

1. No trusted fact without Judge admission.
2. No Judge admission without authoritative evidence lineage.
3. No irreversible promotion without Execution Authority.
4. No cross-project factual memory retrieval.
5. No QIR hint can increase evidentiary confidence.
6. No external provider becomes a trust root by being available.
7. No Blueprint may manufacture knowledge absent from admitted records.
8. Every deep-analysis execution is least-authority and proof-bound.
9. Every adapter is replaceable behind a stable contract.
10. Every user-facing product surface observes the same mission and trust states.

## Next implementation contracts

The next architectural code boundaries should be introduced in this order:

1. `IMissionPlanner` and immutable `MissionPlan`;
2. `IPlanningHintProvider` for QIR with bounded influence;
3. explicit `KnowledgeMemoryClass` and `IMemoryAdmissionGate`;
4. `IGlobalLearningSanitizer`;
5. contradiction/supersession contracts;
6. control-plane state snapshot consumed uniformly by Desktop/API/Mobile;
7. architecture conformance tests that verify forbidden dependency/authority edges.

This order strengthens the brain without coupling it to one model provider, one operating system or one product UI.
