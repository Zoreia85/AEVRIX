# AEVRIX Specialist Lab Coordination

Status: architecture contract v1

AEVRIX has one central authority model and three specialist acquisition/reconstruction labs. The labs are not independent brains and must not create parallel trust systems.

## The three specialist labs

### 1. Web & Online Platform Lab

Primary target: HTTPS links, websites, SaaS, portals and online systems.

Specialist scope includes browser state, DOM/UI, JavaScript/runtime behaviour, routes, authorized network observations, APIs, WebSockets, storage, reports/exports, observable algorithms and integrations.

### 2. Desktop & Offline Software Lab

Primary target: executable/installable/portable software and offline artifacts.

Specialist scope includes package/binary structure, resources, libraries, local storage, processes, IPC, filesystem/registry behaviour, calculations, reports, integrations and controlled dynamic execution where a governed runner exists.

Routing an artifact to this lab does **not** claim that the current Windows host can execute every routed platform format. Native execution support is a separate capability and hard gate.

### 3. Mobile Reverse Engineering, Reconstruction & App Lab

Primary target: Android and Apple mobile applications.

Specialist scope includes APK/AAB/XAPK/IPA intelligence, package metadata, mobile UI/state exploration, Android emulator / Apple simulator or authorized-device observations, mobile integrations, reports, algorithms and differential reconstruction testing.

The Mobile Lab owns its platform-specific implementation. Shared AEVRIX authority remains central.

## Central services shared by all labs

The following are cross-cutting AEVRIX services and must not be independently reimplemented as competing authorities inside a specialist lab:

- Evidence Bus / evidence storage and provenance;
- Evidence Fusion;
- Orchestrator Judge;
- Execution Proof Ledger and execution authority;
- canonical Project Blueprint and trusted-knowledge admission;
- Algorithm Inference methodology and evidence classification;
- Integration Graph semantics;
- report/output equivalence semantics;
- differential validation framework;
- Capability Governance, source pinning and supply-chain controls;
- workspace isolation, credential handling and privacy/security boundaries.

A lab may implement domain-specific adapters that feed these shared services.

## Routing contract

`Aevrix.Core.TargetIntakeRouter` is the preflight routing contract.

- an absolute credential-free HTTPS URL routes to `WebOnline`;
- APK/AAB/XAPK/IPA route to `Mobile`;
- recognized offline executable/package formats route to `DesktopOffline`;
- an unknown artifact remains unroutable and execution is blocked until classification is resolved.

Artifact extensions are hints only. They do not prove format, authenticity, safety, license, provenance or execution eligibility. The receiving lab must verify file content/structure and apply its security gates before parsing or execution.

## Cross-lab handoff contract

One lab remains the **owning lab** for the project. It can delegate a bounded work package to another lab when the target contains a secondary surface.

Examples:

- Mobile owns an app but delegates an authorized API/web-surface investigation to Web/Online;
- Desktop/Offline owns an Electron or embedded-browser application but delegates browser behaviour analysis to Web/Online;
- Web/Online owns a SaaS investigation but delegates a downloadable companion executable to Desktop/Offline;
- any lab can request domain-specific observations needed by the shared Algorithm Inference or Integration Graph processes.

`CrossLabHandoffRequest` intentionally grants only `CandidateEvidenceOnly` authority. A delegated lab cannot use a handoff to:

- take project ownership implicitly;
- mark evidence Trusted;
- promote knowledge into canonical memory;
- overwrite the canonical blueprint;
- bypass Evidence Fusion/Judge;
- bypass capability/runtime security gates.

Candidate evidence returns to the owning project and follows the normal AEVRIX Evidence -> Execution Proof -> Judge -> trusted knowledge/blueprint path.

## Algorithm and result reconstruction

All three labs must treat the functional algorithm as a first-class reconstruction target, not as a side effect of UI replication.

For material functions, the common model is:

`inputs -> constraints/state -> transformation/rules -> calculations -> outputs`

Evidence should distinguish observed facts, confirmed formulas, strongly supported models, unresolved hypotheses and rejected hypotheses. Controlled differential, boundary, metamorphic and property-based tests should be used when applicable.

Reports, PDFs, spreadsheets, labels, exports, generated files and other outputs are part of the behavioural contract and must be tested alongside the UI.

## Equivalence before superiority

The common lifecycle is:

`observe -> evidence -> model -> canonical blueprint -> clean-room reconstruction -> differential validation -> equivalence baseline -> superiority pass`

Performance, UX, architecture or security improvements belong to the Superiority Pass after the baseline behaviour has been measured. A specialist must not hide an unresolved functional divergence by labelling a different behaviour as an improvement.

## Coordination rule for parallel AEVRIX chats/branches

Before starting a material implementation slice, every specialist/front should inspect current canonical `main` and active specialist branches/PRs relevant to the same files or authority boundary.

When overlap exists:

1. preserve the specialist that already owns the domain-specific implementation;
2. move shared contracts into AEVRIX Core instead of duplicating them;
3. use explicit cross-lab handoffs for secondary surfaces;
4. rebase/rebuild safely after privacy-safe canonicalization rather than force history;
5. report exact candidate SHA and the exact gates actually executed.

This document defines coordination semantics only. It does not declare any specialist lab homologated or any reconstruction equivalent to an original target.
