# Roadmap to AEVRIX 0.001

## P0 — Public canonical repository

- [x] Canonical workspace separated from the legacy repository.
- [x] Public licensing/governance baseline.
- [x] Public CI policy prepared.
- [ ] Publish `Zoreia85/AEVRIX` as the only canonical GitHub repository.
- [ ] Verify complete legacy extraction by file hashes.
- [ ] Remove legacy AEVRIX material only after canonical publication and verification.

## P1 — Evidence -> Blueprint

- [x] Integrity-first synthesis implementation in canonical workspace.
- [x] Blueprint reference validation.
- [x] Compile/test on .NET 10 Windows runner.
- [ ] Wire final EngineHost command end-to-end.

## P2 — Secure transport and device identity

- [x] Single secure transport abstraction.
- [x] DPoP client/server validation baseline.
- [x] Device enrollment client baseline.
- [ ] Real TPM/mTLS test.
- [ ] Replay store adapter and backend integration.
- [ ] Entitlement service.

## P3 — Remote Orchestrator/Judge

- [x] Candidate-knowledge governance baseline.
- [x] Adaptive capability broker and bounded model council.
- [x] Bounded Mission Director / specialist swarm.
- [x] Evidence Bus and candidate-fusion pipeline.
- [ ] Deploy minimal public remote-brain service.
- [ ] Add provider adapters and evidence validation pipeline.

## P3.5 — External capability and quantum/hybrid laboratory

- [x] Define admission policy for third-party agent/retrieval/coding frameworks and quantum/hybrid solvers.
- [ ] Add generic governed external-specialist adapter boundary where existing contracts are insufficient.
- [ ] Prototype Microsoft Agent Framework adapter behind `CapabilityBroker`.
- [ ] Prototype project-scoped LlamaIndex retrieval/document adapter with source hashes and provenance binding.
- [ ] Evaluate OpenCode as a pinned, sandboxed coding specialist with diff/build/test evidence.
- [ ] Evaluate LangGraph only for stateful/resumable subworkflows whose value exceeds overlap with native mission/proof state.
- [ ] Keep MetaGPT/CrewAI as comparative laboratory references unless a unique capability gap is proven.
- [ ] Build quantum benchmark harness and classical baselines before connecting paid/remote QPU resources.
- [ ] Prototype simulator-first Qiskit optimization adapter.
- [ ] Prototype D-Wave hybrid optimization adapter behind explicit credential/cost controls.
- [ ] Keep PennyLane quantum/ML work research-only until a concrete AEVRIX problem demonstrates measurable benefit.
- [ ] Promote a quantum/hybrid capability only after reproducible solution-quality, feasibility, end-to-end latency, cost and reliability evidence.

## P4 — Premium UI and public product presentation

- [ ] Consolidate WinUI 3 application under `apps/aevrix-windows`.
- [ ] Command Center / Research Browser / Evidence / Blueprint / AI Analyst / Diagnostics.
- [ ] Real visual regression audit.
- [x] Define GitHub showcase, screenshot provenance and installer visual pipeline.
- [ ] Create canonical `docs/assets` brand/screenshot/diagram tree and visual manifests.
- [ ] Add deterministic synthetic demo projects for safe public screenshots.
- [ ] Capture real software screens from exact reproducible Windows builds.
- [ ] Add screenshot hashes/build metadata and secret/PII checks.
- [ ] Recompose README first viewport around a real AEVRIX product capture.
- [ ] Add Command Center, Mission Control, Evidence, Blueprint, Research Browser and installer gallery.
- [ ] Capture and validate 100%, 125%, 150% and 200% Windows scaling states.
- [ ] Replace concept visuals as each production screen becomes available.

## P5/P6 — Installer and AVA

- [ ] MSI lifecycle.
- [ ] Setup bootstrapper.
- [ ] Apply the canonical eight-stage AEVRIX installer experience and branding.
- [ ] Readiness/security checks use explicit Ready / Warning / Blocked / Requires action states.
- [ ] Repair, upgrade and uninstall receive the same product-quality UX as first install.
- [ ] Authenticode.
- [ ] clean Windows AVA.
- [ ] exact-hash release packaging.
- [ ] Bind final public installer screenshots to the exact validated artifact/hash.
