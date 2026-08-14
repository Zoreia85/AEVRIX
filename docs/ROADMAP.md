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
- [x] Governed QIR learning ledger baseline with independent-project privacy gates.
- [ ] Persist QIR ledger behind an encrypted/project-bound storage adapter.
- [ ] Feed sanitized QIR patterns into planner/capability hints without treating them as evidence.
- [ ] Deploy minimal public remote-brain service.
- [ ] Add provider adapters and evidence validation pipeline.

## P4 — Premium UI

- [ ] Consolidate WinUI 3 application under `apps/aevrix-windows`.
- [ ] Command Center / Research Browser / Evidence / Blueprint / AI Analyst / Diagnostics.
- [ ] Real visual regression audit.

## P5/P6 — Installer and AVA

- [ ] MSI lifecycle.
- [ ] Setup bootstrapper.
- [ ] Authenticode.
- [ ] clean Windows AVA.
- [ ] exact-hash release packaging.
