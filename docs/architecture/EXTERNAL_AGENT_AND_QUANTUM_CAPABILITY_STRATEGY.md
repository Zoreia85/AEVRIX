# External Agent and Quantum Capability Strategy

Status: architecture policy / admission plan. This document does not claim implementation or homologation of any external framework or quantum backend.

## Objective

AEVRIX may use third-party agent frameworks, coding harnesses, retrieval systems and quantum/hybrid solvers when they add measurable capability. None of them becomes the AEVRIX brain, trusted memory authority or evidence authority.

The existing AEVRIX control plane remains sovereign:

```text
mission
  -> MissionDirector
  -> CapabilityBroker
  -> admitted specialist/adapter
  -> bounded candidate output
  -> evidence/provenance validation
  -> OrchestratorJudge
  -> trusted-knowledge promotion only after validation
```

External systems are replaceable capability providers behind governed adapters.

## Admission principles

An external framework or solver is admissible only when all of the following are true:

1. it solves a concrete AEVRIX capability gap;
2. its license is compatible with the public repository and distribution model;
3. the exact version/source is pinned and represented in the SBOM/license inventory;
4. execution can be constrained by the AEVRIX runtime and authority policy;
5. secrets, project data and raw evidence cannot escape the declared data boundary;
6. cancellation, timeout, memory and filesystem/network authority remain bounded;
7. output is treated as candidate information, never as trusted evidence by default;
8. deterministic or independently reproducible validation exists where the capability claims precision;
9. measurable quality/latency/cost/reliability data is captured by the capability fabric;
10. the adapter can be disabled or removed without breaking the core architecture.

Popularity or GitHub star count is not an admission criterion.

## Current framework assessment

### Microsoft Agent Framework — preferred production candidate

**Decision:** evaluate as the first external multi-agent/runtime adapter.

Reasons:

- native .NET and Python support matches the AEVRIX technology split;
- graph workflows, checkpointing, streaming, human-in-the-loop and observability overlap with requirements AEVRIX already treats as production concerns;
- useful as a replaceable provider or workflow runtime behind `CapabilityBroker`, not as a replacement for `MissionDirector` or `OrchestratorJudge`;
- stronger forward path than AutoGen for a new integration.

Planned adapter boundary:

```text
AEVRIX mission task
  -> MicrosoftAgentFrameworkSpecialistAdapter
  -> governed workflow/agent execution
  -> normalized SpecialistResult
  -> AEVRIX evidence boundary / Judge
```

### AutoGen — do not adopt as a new core dependency

**Decision:** no new core adoption. Existing research patterns may be studied, but new implementation work should target Microsoft Agent Framework instead.

Rationale: the official AutoGen project is in maintenance mode and directs new users to Microsoft Agent Framework.

### LangGraph — strong optional orchestration laboratory

**Decision:** evaluate as an optional Python specialist runtime, especially for long-running stateful research workflows that benefit from checkpoint/resume, explicit graph state and human approval points.

AEVRIX already owns mission scheduling, proof state and authority boundaries. Therefore LangGraph must not create a second source of truth for provenance or trusted state. Any persisted LangGraph state is execution state only and must map back to an AEVRIX execution identity.

Preferred use cases:

- bounded research subgraphs;
- resumable document/repository investigations;
- specialist experiments with explicit checkpoints;
- workflow prototyping before a capability is reimplemented natively when justified.

### LangChain — selective utility only

**Decision:** do not make the full LangChain stack a foundational dependency. Admit narrow packages/integrations only when required by a specific adapter.

AEVRIX should avoid importing a broad abstraction layer merely to obtain model/tool wrappers already covered by the capability fabric.

### LlamaIndex — high-value retrieval/document specialist

**Decision:** prioritize an evaluation adapter for document/repository indexing, retrieval, parsing and OCR-oriented workflows.

Candidate boundary:

```text
project-scoped admissible corpus
  -> LlamaIndexRetrievalSpecialistAdapter
  -> retrieved chunks + source references + hashes
  -> AEVRIX evidence/provenance binding
  -> candidate synthesis
```

Requirements:

- project/workspace isolation;
- no implicit global index;
- source hash and locator on every returned unit;
- no retrieved text may bypass evidence classification;
- index deletion must follow workspace/project deletion policy.

### OpenCode — governed coding specialist, not core runtime

**Decision:** evaluate as a sandboxed coding-harness adapter only.

Potential value:

- provider-agnostic coding workflows;
- terminal-based repository changes;
- comparative coding-agent benchmark against AEVRIX-native specialists.

Mandatory controls:

- pinned executable/package hash;
- workspace-only filesystem authority;
- deny network by default and explicitly allowlist when needed;
- bounded command/time/token budget;
- diff capture before any promotion;
- test/build evidence required before accepting generated changes.

### MetaGPT — borrow the SOP/role pattern, avoid runtime lock-in

**Decision:** useful as a design reference for role/SOP decomposition, but not preferred as a production runtime dependency.

AEVRIX already models bounded specialists, mission DAGs, evidence boundaries and a Judge. The valuable idea is disciplined role decomposition; the control plane should remain native.

### CrewAI — optional comparative lab adapter

**Decision:** no core adoption now. It can be used in a benchmark laboratory to compare role-based crews/flows against the native AEVRIX mission model.

If later admitted, it must remain behind the same specialist adapter contract and cannot own trusted memory or cross-project state.

## Quantum and hybrid capability policy

AEVRIX may expose real quantum and quantum-classical hybrid solvers, but only as **experimental specialist capabilities** until repeatable benchmarks prove an advantage for a specific problem family.

Quantum execution is not a generic accelerator for LLM reasoning, OCR, code understanding or evidence validation. It is most plausible for bounded optimization/search subproblems that can be formally encoded.

### Candidate problem families

Prioritize experiments in:

- mission/task scheduling under constraints;
- test-case prioritization and subset selection;
- combinatorial configuration search;
- graph matching / assignment variants;
- resource allocation;
- portfolio selection of specialist/model attempts;
- difficult binary/integer optimization subproblems discovered during research.

Do **not** route ordinary semantic reasoning or factual verification to a quantum backend merely because it is available.

### Initial backend adapters

1. `QiskitOptimizationCapabilityAdapter`
   - simulator-first;
   - QAOA/VQE/Grover-style optimization experiments only where the model is appropriate;
   - real hardware execution remains optional and externally authenticated.

2. `DWaveHybridOptimizationCapabilityAdapter`
   - nonlinear/quadratic/binary/integer optimization where the problem formulation fits;
   - treat returned solutions as optimization candidates that still require deterministic objective/constraint verification.

3. `PennyLaneHybridExperimentAdapter`
   - research-only for hybrid quantum/classical and quantum-ML experiments;
   - no production path until a concrete AEVRIX task demonstrates benefit.

### Quantum result trust boundary

```text
formal problem + constraints + classical baseline
  -> Quantum/Hybrid specialist
  -> candidate solution(s)
  -> deterministic feasibility check
  -> objective recomputation on classical code
  -> benchmark comparison
  -> Evidence Bus as experiment result
  -> Judge
```

A quantum backend never self-certifies correctness. The AEVRIX classical verifier must recompute feasibility and objective values.

### Promotion gate

A quantum/hybrid adapter may move from `Experimental` to `Admitted` only when a benchmark pack demonstrates, for the exact problem family and size range:

- identical input/constraint semantics versus the classical baseline;
- zero unverified constraint violations;
- reproducible run metadata and backend identity;
- solution quality distribution across repeated runs;
- wall-clock latency including queue/network overhead;
- total cost per accepted solution;
- failure/retry rate;
- a statistically defensible improvement in at least one target metric without unacceptable regression in the others.

No claim of "quantum speedup", "greater precision" or "quantum advantage" may appear in product/marketing material unless the benchmark pack supports that exact claim.

## Implementation order

1. Add a generic external specialist adapter contract over the existing capability fabric where necessary.
2. Prototype Microsoft Agent Framework adapter.
3. Prototype LlamaIndex retrieval/document adapter.
4. Add OpenCode coding-harness benchmark adapter if its sandbox requirements can be enforced.
5. Add LangGraph laboratory adapter only for workflows where its durable graph state adds measurable value.
6. Keep MetaGPT/CrewAI as comparative laboratory references unless a unique capability gap appears.
7. Create the quantum benchmark harness and classical baselines before connecting any paid/remote QPU.
8. Add Qiskit simulator adapter.
9. Add D-Wave hybrid adapter behind explicit credentials and cost controls.
10. Promote only after benchmark evidence.

## Non-goals

- replacing the AEVRIX Judge with an external framework;
- allowing an external framework to own global trusted memory;
- importing every popular agent framework simultaneously;
- calling a workflow "quantum" when it only uses classical heuristics;
- using quantum branding as a marketing claim without measurable evidence.
