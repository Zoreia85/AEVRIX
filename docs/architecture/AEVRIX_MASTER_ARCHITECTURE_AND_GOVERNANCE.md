# AEVRIX — Master Architecture, Brain and Governance

Status: CANONICAL ARCHITECTURE GOVERNANCE

This document defines the decision surface for AEVRIX. It exists to keep global architectural work separate from local implementation debugging.

## 1. Scope of master architecture

Changes in the following areas are architectural and must be consolidated here before or together with implementation:

- AEVRIX brain and global intelligence model;
- Orchestrator, Mission Director and specialist topology;
- Judge, confidence policy, evidence admission and promotion policy;
- QIR, learning, prioritization and optimization backends;
- short-term, project, trusted and cross-run memory architecture;
- Evidence -> Execution Proof -> Ledger -> Blueprint provenance;
- plugin/adapter boundaries and external capability governance;
- project, user, subject, workspace and encryption isolation;
- local/remote trust boundaries and Execution Authority;
- Desktop, API/SDK and Mobile product surfaces when they change system boundaries;
- security, privacy, clean-room and authorization policy;
- release/homologation architecture;
- component dependencies and the master roadmap.

Local compiler fixes, CI debugging, test-fixture repair, narrow refactors and cosmetic code changes are implementation concerns and do not redefine this document unless they expose a global architectural defect.

## 2. Product thesis

AEVRIX is one intelligence platform exposed through three governed product surfaces:

1. Windows Desktop — the primary deep local analysis environment.
2. Developer API/SDK — programmatic access to governed jobs, evidence and Blueprint outputs.
3. Mobile Console — secure monitoring, approvals, project interaction and result consumption.

The three surfaces share the same architectural truth. No client may implement an alternate evidence model, alternate authorization model or independent trusted memory.

## 3. Brain topology

The canonical reasoning flow is:

```text
Mission / User Intent
        |
        v
Mission Director / Orchestrator
        |
        +--> Capability admission and policy
        +--> Specialist selection and scheduling
        +--> QIR / optimization policy
        |
        v
Specialist executions
        |
        v
Execution Proof + Evidence
        |
        v
Judge
        |
        +--> reject
        +--> request more evidence
        +--> admit candidate knowledge
        |
        v
Blueprint / Trusted Memory eligibility
```

The Orchestrator coordinates. Specialists observe or derive. The Judge decides admissibility. Trusted memory is never written directly by a specialist.

## 4. Judge

The Judge is a separate policy boundary. It must preserve distinctions between:

- Observed;
- ExperimentallyValidated;
- Inferred;
- VendorClaim.

It must not convert weak evidence into fact by aggregation alone. Promotion requires explicit provenance, scope agreement and policy-compliant confidence.

## 5. Memory model

AEVRIX memory is layered:

- Ephemeral execution state — destroyed with the work lease unless policy says otherwise.
- Project knowledge — scoped to project/workspace and not globally trusted by default.
- Candidate knowledge — derived material awaiting Judge admission.
- Trusted memory — admitted knowledge with immutable provenance references.
- Cross-run learning — only derived from privacy-safe, policy-approved summaries and never by silently mixing project data.

No memory layer may bypass project/user/workspace isolation.

## 6. QIR and quantum policy

QIR is the governed optimization/learning interface of AEVRIX. It may prioritize specialists, experiments and resource allocation, but it does not itself prove correctness.

Quantum or hybrid execution is experimental and adapter-based. It may become eligible only when a reproducible benchmark demonstrates an advantage over the strongest available classical baseline on a real AEVRIX workload.

Mandatory benchmark dimensions:

- solution quality;
- end-to-end latency;
- total cost;
- repeatability;
- failure rate;
- operational complexity.

No marketing claim may describe AEVRIX as quantum-powered unless a production path actually uses a validated quantum/hybrid backend.

## 7. Provenance closure

The canonical knowledge chain is:

```text
Artifact / Target
   -> governed specialist execution
   -> Execution Proof Ledger
   -> Evidence
   -> Judge admission
   -> Blueprint knowledge
   -> export / promotion
```

A reconstructible Blueprint must not contain knowledge that cannot be traced back through this chain.

## 8. External capabilities

All external tools, models, repositories, MCP servers, local runtimes and future providers are adapters behind governed boundaries.

Admission requires, as applicable:

- explicit capability contract;
- source identity and pinned revision;
- content digest;
- license status;
- security review;
- runtime approval;
- project/user/workspace scope;
- network scope;
- data exposure limit;
- deterministic or bounded failure behavior.

Adapters do not receive implicit trust because they are popular, open source or locally installed.

## 9. Security and privacy invariants

AEVRIX must remain fail-closed for protected operations.

Global invariants:

- no implicit cross-project reads;
- no implicit cross-user reads;
- no secrets in public Git history;
- no personal metadata in canonical automation commits;
- no credential theft or session theft;
- no authentication bypass;
- no CAPTCHA bypass;
- no DRM/license bypass;
- no malicious persistence;
- no exploit deployment as a product capability;
- no claim of isolation unless the enforcement mechanism is actually active and attested.

## 10. Architecture decision rule

A proposed architectural change should be accepted only when it improves at least one of:

- correctness;
- security;
- provenance;
- isolation;
- extensibility;
- operability;
- measurable performance;
- product coherence;

without creating an unjustified regression in the others.

New dependencies must have a clear architectural role. New complexity without evidence of value is rejected.

## 11. Component ownership boundaries

- Core contracts: neutral identities, storage boundaries, IPC contracts and shared invariants.
- EngineHost: isolated local execution boundary.
- Remote Orchestration: missions, specialists, Judge, evidence reasoning, QIR and Blueprint composition.
- Remote Security: authentication, request proof, anti-replay and transport security policy.
- Execution Authority: independent authorization and promotion trust domain.
- Product clients: presentation, user workflow and safe invocation of canonical contracts.

Clients are not security authorities.

## 12. Roadmap governance

The master roadmap is risk-weighted, not file-count based. A stage is complete only when its exit evidence exists.

Priority order:

1. brain/orchestration correctness;
2. provenance and Judge closure;
3. security and isolation boundaries;
4. Windows end-to-end product path;
5. public API/SDK contract;
6. mobile console;
7. release/signing/update architecture;
8. advanced optimization and experimental quantum adapters.

Roadmap percentages must not be inflated by documentation-only or test-only work.

## 13. Definition of homologated

HOMOLOGATED is a release state, not a development adjective.

It requires the exact delivered artifact to have reproducible evidence for all applicable installation, launch, runtime, security, recovery, update, signing, usability and functional gates. A component may be individually validated while the product remains NOT_HOMOLOGATED.

## 14. Decision log discipline

Material architectural decisions should be captured in GitHub using either this document, an ADR under `docs/architecture/adr/`, or the master roadmap. Implementation PRs should reference the architectural decision they realize when the change affects a global boundary.

The Git repository is the canonical technical record. Chat discussion is a design input; architecture becomes authoritative only when consolidated in the repository.
