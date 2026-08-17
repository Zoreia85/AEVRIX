# AEVRIX Mobile Provenance & Local MobSF v0.5

## Purpose

This layer strengthens two independent trust questions:

1. **Where did an analysis tool/release come from, and what provenance evidence exists?**
2. **Can MobSF add security evidence without exposing proprietary mobile artifacts to third-party infrastructure?**

It does not convert provenance metadata into a capability score and it does not promote MobSF or any upstream tool automatically.

## GitHub provenance intelligence

The read-only GitHub client now includes:

- latest release metadata;
- individual release-asset metadata;
- commit metadata including GitHub's signature-verification object;
- public user artifact-attestation lookup by SHA-256 subject digest;
- public organization artifact-attestation lookup by SHA-256 subject digest.

Release assets can expose a GitHub-provided SHA-256 digest. `ToolProvenanceSnapshot` preserves that digest as evidence.

Commit signature verification, release-asset digest presence, and artifact-attestation presence are intentionally separate facts. An attestation's presence is recorded as `PRESENT_UNVERIFIED`; the Mobile Lab does not call it cryptographically verified merely because an API returned an attestation record.

## MobSF local REST adapter

The MobSF adapter implements the static-analysis REST sequence against a loopback endpoint only:

1. upload an authorized APK/IPA;
2. start the static scan;
3. retrieve JSON report evidence;
4. delete the local scan record when cleanup is enabled.

### Hard privacy controls

- endpoint must pass the existing loopback-only `MobSFLocalEndpoint` policy;
- server network isolation must be explicitly confirmed by the caller;
- API key is required but excluded from durable evidence hashes;
- source artifact requires explicit authorization;
- bytes are SHA-256 verified against the authorized artifact record before upload;
- upload is streamed in bounded chunks instead of being loaded wholesale into memory;
- only the allowlisted MobSF API paths are callable;
- response sizes, upload sizes and timeouts are bounded;
- no redirect following is implemented;
- scan cleanup is attempted even when report processing fails.

The server itself must be deployed under AEVRIX containment policy. The client-side `network_isolation_confirmed` gate is an assertion boundary, not proof that the server namespace/firewall was actually isolated; central environment evidence must establish that separately.

## Provenance interpretation

A tool is **not** admitted because:

- its GitHub commit signature is verified;
- its release asset exposes a SHA-256 digest;
- an attestation record exists;
- its repository is popular or mature.

Those facts strengthen credibility/auditability inputs. Functional efficacy, precision gain, containment, reliability, performance, duplication and cost-benefit still require benchmarks through central Capability Governance.

## Next evidence step

The next benchmark should pin official releases for JADX, Apktool, MobSF and Frida, store upstream release/commit/attestation snapshots, fingerprint installed local executables, then compare local fingerprints to the strongest available upstream digest/attestation evidence without silently claiming equivalence when the downloadable release asset is an archive rather than the executable itself.
