# AEVRIX Execution Authority Service

Status: **development / NOT_HOMOLOGATED**

The Execution Authority Service is an independent trust domain for monotonic execution-ledger heads and signed promotion attestations. It does not become an AEVRIX brain, does not evaluate evidence, and does not replace the Orchestrator Judge.

## Responsibility boundary

The service is allowed to know only the minimum material needed to prove authority:

- project, run and execution identifiers;
- capability identifiers;
- execution-ledger head count and SHA-256;
- artifact-manifest SHA-256;
- validation SHA-256;
- Judge-decision SHA-256;
- promotion SHA-256;
- canonical promotion-evidence SHA-256;
- client/key identifiers and bounded anti-replay metadata.

It does **not** store prompts, model responses, source evidence, user files, browser/session state, credentials, artifact contents or private AEVRIX knowledge.

## Trust split

The local/remote brain produces the governed `ExecutionProofLedger`. Its encrypted snapshot can be rolled back independently of the Execution Authority.

The Authority maintains the external monotonic head. A project head advances exactly one record through compare-and-swap. A submitted promotion can be attested only when its ledger head exactly equals the Authority's current head.

The resulting attestation is signed by an ECDSA P-256 key whose private component exists only in the Authority runtime secret boundary. A client verifies the signature against a separately pinned public key and key id.

Therefore:

- local snapshot rollback is detected by head divergence;
- a forged local head is not authoritative;
- a database head that differs from submitted promotion evidence is rejected;
- a copied evidence digest is not an authorization token;
- a signed attestation is bound to one project/run/execution/evidence/head and a bounded validity window.

## Request authentication and replay resistance

Authenticated endpoints use HMAC-SHA-256 over a canonical request:

```text
AEVRIX-AUTHORITY-REQUEST-V1
<METHOD>
<ESCAPED_PATH>
<UNIX_TIMESTAMP>
<NONCE>
<BODY_SHA256>
```

Required headers:

- `X-AEVRIX-Client-Id`
- `X-AEVRIX-Timestamp`
- `X-AEVRIX-Nonce`
- `X-AEVRIX-Body-SHA256`
- `X-AEVRIX-Request-Signature`

The server verifies timestamp, body hash and HMAC before reserving the nonce. A correctly signed replay is rejected by the nonce store. Query strings and fragments are not accepted on this authority API.

Remote clients require HTTPS. Plain HTTP is available only as an explicit loopback test fixture.

## Promotion attestation

Protocol label:

`AEVRIX-PROMOTION-ATTESTATION-V1`

The signed canonical payload binds:

- protocol version;
- signing key id;
- project id;
- run id;
- execution id;
- promotion-evidence digest;
- authoritative head entry count;
- authoritative head SHA-256;
- issuance and expiration timestamps;
- cryptographic nonce.

Signature format is ECDSA P-256 / SHA-256 using an RFC 3279 DER sequence. The attestation also carries the SHA-256 fingerprint of the DER SubjectPublicKeyInfo so the client can require the exact pinned public key.

The cross-language `.NET <-> Go` canonical promotion-evidence vector currently resolves to:

`2064dc617a9710d1ff7f96c14628b58a28740e15cbcc3b16e680ee95d7acea8b`

This vector is asserted independently by both implementations.

## PostgreSQL authority state

The Go service maintains four minimal tables:

1. `execution_authority_heads` — current project head;
2. `execution_authority_head_events` — immutable CAS-transition history and idempotency key;
3. `execution_authority_nonces` — bounded replay protection;
4. `execution_authority_attestations` — signed attestation audit records.

Head transitions run inside a transaction and acquire a transaction-scoped PostgreSQL advisory lock derived from the project id. `READ COMMITTED` is deliberate: after a losing contender waits for the project lock, it observes the winner's committed head and then fails the exact predecessor comparison. Unique violations, deadlock/serialization conflicts and stale predecessors are classified as authority conflicts, never silently retried into a fork.

A repeated request id is idempotent only when project, client, predecessor and next head are byte-for-byte equivalent at the protocol level. Reusing the request id for a different transition fails closed.

## Public HTTP surface

Only the following routes exist:

- `GET /healthz`
- `GET /v1/public-key`
- `GET /v1/projects/{projectID}/head`
- `POST /v1/projects/{projectID}/head/advance`
- `POST /v1/promotions/attest`

There is no generic SQL endpoint, administrative mutation API, secret-returning endpoint, artifact endpoint, shell, model endpoint or browser/session endpoint.

JSON is strict and bounded. Unknown fields are rejected. Responses use `no-store`; the runtime limits headers and HTTP read/write/idle windows.

## Secret boundary

The repository contains **no private signing key, HMAC client secret, database password or connection string**.

The runtime consumes only secret-bearing environment variables:

- `DATABASE_URL`
- `AEVRIX_AUTHORITY_CLIENT_SECRET_B64`
- `AEVRIX_AUTHORITY_SIGNING_KEY_PKCS8_B64`

Non-secret identifiers include:

- `AEVRIX_AUTHORITY_CLIENT_ID`
- `AEVRIX_AUTHORITY_SIGNING_KEY_ID`

The public ECDSA key may be distributed separately and is additionally exposed by `/v1/public-key` for fingerprint verification. A production client must still pin the expected key rather than implicitly trusting the network response.

## Current validation evidence

The `.NET` authority client compiles inside the normal AEVRIX orchestration test suite and verifies HMAC construction, CAS conflicts, public-key pinning, signed attestation binding, forged-signature rejection, head mismatch rejection, HTTPS policy and secret non-serialization.

The Go service validation gate uses Go 1.26.5 and performs:

- `gofmt`;
- locked module resolution (`go mod tidy` / `go.sum`);
- `go test -race ./...`;
- `go vet ./...`;
- production binary build.

A PostgreSQL 18 service container is used for real store integration tests. The current gate passed exact request idempotency, modified-request conflict detection, stale predecessor rejection, a simultaneous fork race with exactly one winning CAS, nonce replay rejection and duplicate attestation-evidence conflict handling.

The public repository scanner has been extended to inspect `.go`, `.mod` and `.sum` files for the same branding/private-key/hard-coded-secret policy applied to other public source formats.

## Render homologation infrastructure

A dedicated Render Postgres instance named `aevrix-execution-authority-db` has been created in the existing shared infrastructure workspace solely for the AEVRIX authority homologation path. It is not shared with another application service.

The database currently uses Render's free homologation plan and must not be treated as a production authority or durability SLA.

The Authority web service is intentionally **not** created until its database connection can be injected without exposing or inventing credentials. The available Render connector does not expose the sensitive internal connection string or a `fromDatabase` binding in its web-service creation contract. The approved Render architecture is to bind the service to the database's internal connection string; no password or URL will be copied into GitHub to bypass that boundary.

## Threat-model limits

This service creates an independent rollback domain relative to an AEVRIX client machine. It does **not** claim that a compromised Authority service, compromised signing key or privileged database administrator is harmless.

In particular:

- database compromise can corrupt authority state;
- service compromise can misuse an in-memory signing key;
- compromise of both authenticated client and Authority state can manufacture requests the Authority may sign;
- a free single database is not a high-availability witness.

Production hardening therefore still requires key custody outside ordinary application secrets (KMS/HSM or equivalent), privileged database controls, independent backups/witnessing, key rotation/revocation and an independently verified queue-side attestation consumer.

## Remaining gates

1. deploy the Go Authority service with a secure Render database binding;
2. run live HTTPS HMAC/replay/CAS/attestation interoperability tests against homologation;
3. store and pin the homologation public key in the approved client configuration without exposing private material;
4. connect `AnchoredExecutionProofStore` to `RemoteExecutionAuthorityClient` in a real AEVRIX execution path;
5. bind the canonical bot/noreply patch queue to independently verified promotion attestations;
6. move production signing to a stronger key-custody boundary and validate rotation/revocation;
7. run recovery, rollback, database restore and concurrent multi-client hostile tests;
8. complete SBOM/supply-chain and final release gates.

Passing this phase's unit/integration gates does **not** make AEVRIX homologated.
