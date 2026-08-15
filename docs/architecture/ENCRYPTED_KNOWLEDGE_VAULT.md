# Encrypted Project Knowledge Vault

`EncryptedProjectKnowledgeRepository` is the production-oriented persistence boundary for Judge candidate knowledge and validation records. It implements `ICandidateKnowledgeRepository` without embedding a storage key in source code.

Security properties:

- AES-256-GCM encryption at rest using a project-scoped key supplied by `IProjectKnowledgeKeyProvider`;
- fresh 96-bit nonce per write and 128-bit authentication tag;
- authenticated associated data binds envelope version, record kind, record id and project id;
- record filenames are SHA-256 digests rather than raw knowledge/validation ids;
- bounded envelope/payload sizes before allocation/deserialization;
- malformed JSON, authentication-tag mismatch and invalid key size fail closed;
- plaintext and copied key material are zeroed after cryptographic operations;
- candidate and validation ids are immutable: an existing id cannot be rebound to different content;
- validation evidence cannot escape the authoritative candidate evidence set;
- promotion reloads the stored candidate and stored validation record and recomputes the permitted trust state instead of trusting the caller;
- writes use a temporary file followed by atomic replacement within the vault directory;
- in-process per-record locks serialize competing updates.

The repository does **not** generate or persist project keys. A deployment adapter must provide `IProjectKnowledgeKeyProvider`, backed by an appropriate local/remote secret-management boundary. Keys must be exactly 256 bits. No key, credential, token, private key, browser profile or session material is committed to this repository.

This component encrypts project knowledge but does not make it globally learnable. Cross-project/global learning remains governed by the Evidence Bus eligibility and sanitization rules.
