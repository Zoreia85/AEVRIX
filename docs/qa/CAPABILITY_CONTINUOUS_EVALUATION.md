# AEVRIX Continuous Capability Evaluation

Status: mandatory QA/governance policy.

## Purpose

AEVRIX does not keep a framework, library, model, solver, OCR engine, browser driver, reverse-engineering utility, agent runtime, quantum backend or other capability merely because it was once useful or popular. Every non-trivial capability must continuously justify its presence with evidence.

The objective is to maximize reverse-engineering quality, precision, rigor, auditability and reproducibility while minimizing unnecessary cost, latency, attack surface, maintenance burden and architectural duplication.

## Core rule

Every capability is a replaceable, scored component. No capability is permanently privileged. The AEVRIX control plane, evidence model and trust boundaries remain sovereign.

A capability may be retained only when:

1. it closes a concrete capability gap or measurably improves an existing task family;
2. its output can be independently checked or bounded;
3. its data, security and authority boundaries are enforceable;
4. its license and distribution model remain compatible;
5. its measurable benefit is proportional to its total cost;
6. it does not create a second uncontrolled source of truth;
7. benchmark evidence remains current enough for its risk tier.

## Score model

Each capability is scored from 0 to 10 on the following weighted dimensions. Weighted total is normalized to 0-100.

| Dimension | Weight | Meaning |
|---|---:|---|
| Efficacy | 18 | How often the capability materially improves task success. |
| Precision gain | 14 | Improvement in correctness, reconstruction fidelity, defect detection or solution quality versus the current baseline. |
| Auditability | 13 | Ability to preserve provenance, hashes, deterministic checks, reproducible evidence and explainable execution records. |
| Security controllability | 12 | Ability to enforce sandbox, network, filesystem, secret, cancellation and authority boundaries. |
| Necessity | 10 | Degree to which the capability fills a real gap rather than duplicating existing AEVRIX functionality. |
| Reliability | 8 | Stability, failure rate, retry behavior and consistency across repeated runs. |
| Credibility / maturity | 7 | Maintenance quality, release discipline, documentation quality, security posture and ecosystem maturity. |
| Cost-benefit | 7 | Benefit relative to monetary, token, compute, licensing, operational and human-maintenance cost. |
| Performance | 5 | Wall-clock latency, throughput, startup overhead and resource efficiency. |
| Maintainability | 3 | Upgrade burden, dependency complexity and adapter stability. |
| Interoperability | 2 | Fit with AEVRIX contracts, platforms, runtimes and evidence structures. |
| Uniqueness | 1 | Whether the capability contributes something that cannot be obtained more simply elsewhere. |

The registry stores observed scores plus evidence confidence. A high raw score with weak evidence is not sufficient for admission.

## Evidence confidence

`evidence_confidence` is 0.00-1.00 and reflects the strength of the benchmark pack supporting the score.

Minimum confidence:

- `PREFERRED`: 0.85
- `ADMITTED`: 0.75
- `CONDITIONAL`: 0.60
- below 0.60: laboratory/research only

Evidence confidence must consider repeat count, task diversity, reproducibility, baseline quality, environmental control, source hashes and freshness.

## Lifecycle states

- `CANDIDATE`: researched but not integrated.
- `LAB`: integrated only in a controlled benchmark/laboratory path.
- `CONDITIONAL`: useful in a bounded task family but not globally preferred.
- `ADMITTED`: approved for declared production task families.
- `PREFERRED`: current champion for one or more task families.
- `WATCH`: retained temporarily after regression, ecosystem concern or weak cost-benefit.
- `QUARANTINED`: blocked from normal execution pending security/trust investigation.
- `REMOVE`: removal planned because the capability no longer justifies its burden.
- `REJECTED`: evaluated and not admitted.

## Promotion bands

Subject to all hard gates and confidence requirements:

- 85-100: eligible for `PREFERRED`.
- 75-84.99: eligible for `ADMITTED`.
- 60-74.99: eligible for `CONDITIONAL`.
- 45-59.99: `WATCH` or `LAB` only.
- below 45: `REMOVE` or `REJECTED` unless a documented strategic experiment requires temporary retention.

Promotion is task-family-specific. A capability can be preferred for repository retrieval and simultaneously rejected for browser reconstruction.

## Hard gates

A numeric score cannot override any hard gate. A capability fails admission when any applicable gate is false:

- license compatibility verified;
- exact version/source/hash pinning available;
- SBOM/third-party notice representation available;
- secrets can be isolated;
- filesystem/network/process authority can be bounded;
- cancellation and timeout can be enforced;
- output can be attributed to execution identity and source inputs;
- evidence/provenance cannot be silently bypassed;
- a reproducible validation or independent verification method exists for precision claims;
- disable/remove path exists without breaking the AEVRIX core.

Security-critical failure immediately changes state to `QUARANTINED` regardless of score.

## Champion-challenger testing

For each important task family, AEVRIX maintains:

- a `champion`: current preferred implementation;
- zero or more `challengers`: candidate alternatives;
- a fixed regression corpus;
- adversarial/edge-case corpus;
- replayable execution configuration;
- expected/verified outputs or evaluation rules.

A challenger only replaces the champion when evidence shows a meaningful improvement and no unacceptable regression in precision, auditability, security or cost.

No framework is allowed to win simply by producing more output. Quality is measured against verified task objectives.

## Required benchmark dimensions

Depending on the capability family, benchmark packs should include as applicable:

- correctness / exact match / constraint satisfaction;
- recovered feature coverage;
- defect and vulnerability discovery precision/recall;
- false-positive and false-negative rates;
- reconstruction fidelity;
- reproducibility across repeated runs;
- p50/p95/p99 latency;
- peak memory and CPU/GPU use;
- token/model/API/QPU cost;
- total wall-clock cost including queues/network;
- failure, retry and timeout rate;
- provenance completeness;
- evidence hash completeness;
- sandbox/network boundary compliance;
- secret-leak tests;
- degradation under malformed, hostile or incomplete inputs;
- operator effort required per accepted result.

## Continuous re-evaluation triggers

A capability must be re-scored when any of these occurs:

1. its pinned version changes;
2. a dependency/security advisory affects it;
3. its license or distribution terms change;
4. the AEVRIX adapter or runtime boundary changes;
5. the current champion changes;
6. a new representative regression corpus is added;
7. precision/correctness degrades beyond the task-family tolerance;
8. p95 latency or total cost regresses materially versus its admitted baseline;
9. failure/retry rate materially increases;
10. a new alternative is credibly capable of replacing it;
11. at least 7 days have elapsed for high-change external services, or 30 days for pinned local tools, without a fresh evidence review;
12. every 100 accepted executions for high-impact production capabilities, whichever comes first.

A daily governance workflow validates registry integrity and evidence freshness. Expensive benchmarks may run less frequently; the registry must mark stale evidence rather than silently treating it as current.

## Cost-benefit discipline

Total cost includes more than subscription price:

- API/model/QPU charges;
- CPU/GPU/RAM/storage/network consumption;
- latency added to a mission;
- operational complexity;
- attack surface;
- dependency/update burden;
- debugging effort;
- CI minutes;
- additional evidence-storage volume;
- lock-in and migration cost.

A more expensive capability may be preferred when it produces sufficiently stronger verified results. A cheaper capability is not automatically better. The target is value per accepted, auditable result.

## Automatic downgrade and removal

The system should recommend or enact downgrade when evidence shows:

- score below its current lifecycle band;
- stale evidence beyond allowed freshness;
- persistent regression versus champion baseline;
- duplicated functionality with no measurable unique benefit;
- unacceptable security or provenance behavior;
- maintenance burden exceeding demonstrated value.

Removal must preserve historical manifests and benchmark evidence so past mission results remain auditable.

## Special rule for quantum/hybrid capabilities

Quantum/hybrid solvers are subject to this same scorecard plus mandatory classical verification. They are not credited for novelty. They only receive precision/performance/cost points from measured, repeatable task-family results including queue and remote-service overhead.

No quantum capability may be marketed as faster, more precise or superior unless the corresponding benchmark pack supports that exact claim.

## Initial evaluation targets

The first external capability cohort is registered for continuous evaluation:

- Microsoft Agent Framework;
- LlamaIndex;
- OpenCode;
- LangGraph;
- LangChain components;
- MetaGPT;
- CrewAI;
- AutoGen;
- Qiskit Optimization;
- D-Wave Hybrid;
- PennyLane.

Initial registration does not mean admission. Each starts without production authority until its hard gates and benchmark evidence justify a lifecycle transition.

## Reporting requirement

Every development/homologation cycle touching a capability should report:

- lifecycle state;
- latest weighted score;
- evidence confidence;
- task families tested;
- champion baseline used;
- deltas in precision, latency, reliability and cost;
- hard-gate status;
- freshness/staleness;
- keep / improve / restrict / quarantine / remove recommendation;
- exact commit, adapter version and evidence hashes.

The objective is not to maximize the number of tools. It is to continuously converge on the smallest set of capabilities that produces the strongest verified AEVRIX result.