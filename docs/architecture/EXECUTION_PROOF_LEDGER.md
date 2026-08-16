# AEVRIX Execution Proof Ledger

Status: **development / NOT_HOMOLOGATED**

The Execution Proof Ledger is the authoritative evidence boundary between an AEVRIX capability execution and any later artifact promotion. It is intentionally separate from diagnostics, observability and specialist telemetry. Telemetry may explain an execution; it cannot authorize promotion.

## Authority model

A promotable execution must form one ordered chain:

1. `Started`
2. `Completed`
3. `ValidationCompleted`
4. `JudgeDecided`
5. `PromotionAuthorized`
6. `PromotionCommitted`

The sequence is authoritative. `ObservedAt` is audit metadata and does not replace ledger order.

Every downstream stage remains bound to the same:

- project;
- run;
- execution;
- capability class and capability id;
- input SHA-256;
- authority SHA-256;
- result SHA-256;
- isolation/attestation SHA-256 when present;
- artifact-manifest SHA-256 when present;
- validation SHA-256;
- Judge-decision SHA-256;
- promotion SHA-256.

A stage cannot silently substitute any prior digest. Cross-project, cross-run and cross-capability replay fails closed.

## Content minimization

The ledger stores opaque identifiers, bounded metadata and cryptographic digests. Its record contract does **not** contain:

- prompts;
- model responses;
- raw tool output;
- secret values;
- credentials or tokens;
- artifact contents;
- browser/session material.

Sensitive execution material belongs in its governed storage boundary. The ledger proves what was evaluated without becoming a second repository of sensitive payloads.

## Hash chain

Each `ExecutionProofRecord` contains:

- monotonically increasing sequence;
- previous-record SHA-256;
- canonical event data;
- record SHA-256.

`ExecutionProofLedger.VerifySnapshot` recomputes the complete chain and semantic state machine. It detects record modification, previous-hash corruption, reorder, replay and invalid stage transitions.

A hash chain alone cannot detect removal of a valid suffix when the attacker can also replace the stored head. A separately retained `ExecutionProofHead` therefore contains both entry count and head SHA-256. Verification against that external head detects tail truncation.

## Encrypted persistence

`EncryptedExecutionProofStore` persists project-bound snapshots with AES-256-GCM using the existing `IProjectKnowledgeKeyProvider` boundary.

Properties:

- exactly 256-bit project keys;
- fresh 96-bit nonce per save;
- 128-bit authentication tag;
- authenticated associated data binds protocol version and project id;
- project identifiers are hashed in filenames;
- bounded envelope and record counts;
- malformed envelopes and authentication failures fail closed;
- mixed-project snapshots are rejected;
- plaintext and copied key buffers are zeroed after cryptographic operations;
- temporary-file replacement prevents a partially serialized snapshot from becoming the selected snapshot.

`PersistentExecutionProofLedger` rehydrates by replaying every event through the same semantic state machine and requires deterministic reproduction of all stored record hashes before the recovered state becomes authoritative.

## Rollback resistance

Valid authenticated ciphertext can still be old ciphertext. Encryption alone therefore does not prove freshness.

`IExecutionProofHeadAnchor` defines a monotonic compare-and-swap authority that must live **outside the rollback domain of the encrypted snapshot**. `AnchoredExecutionProofStore` requires the encrypted snapshot and this external anchor to agree on every load.

The write order is deliberately:

1. verify candidate chain and exact predecessor;
2. persist the authenticated encrypted snapshot;
3. compare-and-swap the external head from the exact predecessor to the candidate head.

If execution stops after step 2 but before step 3, the snapshot is ahead of the anchor and load fails closed. Retrying the exact save can complete the CAS. If an older but authentic encrypted snapshot is restored while the external anchor remains newer, load also fails closed.

A production anchor is **not** implemented by pretending that another file beside the snapshot is independent. Deployment must bind `IExecutionProofHeadAnchor` to an authority outside that rollback domain, for example a protected remote append/CAS service or another platform-specific protected monotonic state with equivalent guarantees. The specific adapter requires separate threat-model review and hostile tests.

## Promotion evidence

`PromotionEvidenceEnvelope` is emitted only after the ledger contains:

- successful artifact-bearing execution;
- successful validation;
- an approved Judge decision;
- explicit promotion authorization.

The envelope binds the artifact manifest, validation, Judge decision, promotion digest, authorization-record hash and current ledger head. It exposes a deterministic SHA-256 digest suitable for a later independent signature/attestation layer.

The digest by itself is **not** an authorization token. Any caller can copy a hash string. A remote patch queue or deployment service must not trust a declared envelope digest until it can independently verify its origin through a separately trusted signature, attestation or proof-authority lookup.

## GitHub promotion boundary

The existing AEVRIX bot/noreply patch queue remains the canonical source promotion mechanism. This ledger does not create a competing source-control pipeline.

Current rule:

- validation branches/PRs may prove compilation, tests and source policy;
- canonical source promotion continues through the authoritative bot patch queue;
- the queue must **not** be modified to accept a self-declared execution-proof hash as authorization;
- future queue binding requires an independently verifiable execution-authority proof, with verification performed by the queue itself.

Until that verifier exists, source CI evidence and runtime Execution Proof Ledger evidence are complementary but not cryptographically fused across the GitHub trust boundary.

## Evidence in this development gate

The validation branch was rebuilt on the then-current privacy-safe `main` root after a concurrent root refresh. The exact six implementation/test blobs were transplanted without importing the previous validation history.

The Windows validation gate for the rollback-resistant implementation passed:

- Windows Core: 37/37;
- Remote Security: 4/4;
- Remote Orchestration: 219/219;
- total: 260/260;
- Source Policy: PASS.

Hostile coverage includes payload/hash tampering, chain break, reorder, tail truncation, replay, event-id reuse, missing artifact evidence, rejected Judge decisions, forged promotion digests, wrong encryption keys, ciphertext modification, cross-project persistence, deterministic rehydration, valid-ciphertext rollback, stale anchor CAS, split snapshot/anchor state and raw-content-field regressions.

These results validate the current implementation boundary. They do **not** constitute final AEVRIX product homologation.

## Remaining gates

1. production `IExecutionProofHeadAnchor` adapter in an independently protected rollback domain;
2. independent signature/attestation over `PromotionEvidenceEnvelope`;
3. verifier integration into the canonical bot/noreply promotion queue without storing private signing material in the repository;
4. real end-to-end sandbox-agent execution producing an artifact manifest and ledger chain;
5. real MCP and model executions recorded through the same authoritative ledger boundary;
6. recovery, crash and concurrency testing against the selected production persistence/anchor infrastructure;
7. signed capability approvals, SBOM and final release-gate evidence.
