# AEVRIX Artifact Normalization and Cryptographic Quarantine

Status: architecture contract v1

AEVRIX must be able to study heterogeneous software, data and protocol surfaces without silently dropping evidence just because a target uses an unfamiliar container, transport or storage format. This contract extends specialist routing with a bounded normalization/quarantine pipeline.

## Broader intake surface

The shared router recognizes representative protocol families in addition to HTTPS:

- HTTP/HTTPS;
- WS/WSS;
- gRPC/gRPC-over-secure transport;
- FTP/FTPS/SFTP;
- MQTT/MQTTS and AMQP/AMQPS;
- IPFS/IPNS content-addressed resources;
- explicitly declared blockchain RPC endpoints over HTTP(S) or WebSocket(S).

Cleartext transports are routable for authorized observation, but are marked as transport-risk targets. Routing never authorizes credentials or sensitive payloads over cleartext.

Offline routing now covers representative families including executables/installers, archives, disk and VM images, native/bytecode artifacts, WebAssembly, databases, documents, structured data, packet captures, smart-contract sources, blockchain artifacts, firmware and Android/Apple application packages.

The list is intentionally extensible. AEVRIX must not claim universal format support from filename suffixes alone. Unknown material remains fail-closed until magic/structure-based classification identifies a safe parser or adapter.

## Canonical normalization pipeline

Captured artifacts enter a read-only quarantine before any parser or converter receives them. The default plan is:

`capture -> SHA-256 -> magic/structure detection -> metadata -> encryption detection -> family-specific parser -> canonical intermediate -> nested-artifact scan -> Evidence`

The original bytes and SHA-256 are preserved. Conversion creates a derivative; it never replaces the source of truth.

Family-specific operations can include:

- bounded archive enumeration/decompression/extraction;
- read-only disk/package inspection;
- document structure and embedded-media extraction;
- structured-data normalization;
- read-only database schema/data inventory;
- static disassembly of native code, bytecode, WebAssembly and firmware;
- packet-capture parsing;
- blockchain ABI/contract/data structure parsing;
- recursive nested-artifact discovery.

## Quarantine resource controls

The default contract is read-only, offline and non-executing. It caps input size, expanded size, file count and nesting depth to reduce archive bombs, malformed-container attacks and parser abuse.

A parser or converter does not gain network or execution authority merely because it can interpret a format. Dynamic execution is a separate specialist/runtime decision with its own sandbox and execution-proof gates.

## Cryptographic quarantine

Encrypted material is not discarded. AEVRIX records its hash, structure, metadata and encryption state, then pauses the plaintext-dependent branch behind `CryptographicQuarantineRequest`.

Permitted production access modes in the public contract are deliberately narrow:

1. `AnalyzeOnly` — inventory the encrypted object without plaintext access;
2. `DecryptWithVaultMaterial` — use cryptographic material already supplied/owned by the authorized project through a vault reference;
3. `ValidateRememberedCandidates` — validate a small bounded set of user-supplied remembered candidates, without generating a dictionary or brute-force search space.

No raw key material or remembered candidate values belong in Git, Evidence, Blueprint, logs or the request object. The request stores only references/counts and requires an authorization-evidence id tied to the project.

Plaintext produced in quarantine remains Candidate material. Promotion outside quarantine requires the normal Evidence/Judge path.

## What is intentionally not implemented as a general service

AEVRIX must not turn reverse-engineering collection into an unrestricted credential/cryptographic cracking platform. The public core therefore does not provide generic brute-force key search, DRM defeat, signature bypass, authentication bypass or access-control circumvention.

For legitimate recovery of owned/authorized data, AEVRIX can use supplied vault material or bounded user-supplied candidate validation inside quarantine. If a future recovery adapter is proposed, it must pass Capability Governance, authorization, legal/scope controls, resource limits and independent security review before admission.

## Quantum boundary

Quantum/hybrid tooling remains useful for research and benchmark work, but novelty is not a production authorization. Production cryptographic access requests reject quantum cryptanalysis. Quantum cryptanalysis work is restricted to synthetic/toy benchmark fixtures where classical baselines, cost, accuracy and reproducibility can be measured safely.

If a future quantum capability demonstrates a legitimate non-circumvention advantage (for example optimization of parser scheduling, test selection or resource allocation), it can be evaluated through the normal champion-vs-challenger capability process.

## Blockchain boundary

A declared blockchain RPC endpoint routes to Web/Online because collection is network/protocol work. Static smart-contract source, ABI and chain artifacts route to Desktop/Offline for read-only parsing. Mobile or Desktop projects can delegate their blockchain surface to Web/Online without losing project ownership.

Blockchain collection may inventory public/authorized chain state, RPC schemas, contract bytecode/ABI, traces and application integration behaviour. Routing does not authorize transaction signing, transaction broadcast, wallet extraction or transfer of assets.

## Fidelity objective

The purpose of normalization is to prevent blind spots in reverse-engineering studies. AEVRIX should preserve enough provenance to trace every normalized observation back to the exact captured bytes, transport observation or authorized runtime event. Unsupported or encrypted material must remain visible as an unresolved evidence gap rather than being silently omitted from a Reconstruction Fidelity assessment.
