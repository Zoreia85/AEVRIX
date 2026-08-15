# Contributing to AEVRIX

Thank you for improving AEVRIX.

## Before opening a change

1. Keep the clean-room and authorization boundary intact.
2. Do not add secrets, target credentials, production certificates, private evidence or proprietary third-party source material.
3. Add deterministic tests for behavior changes whenever practical.
4. Preserve the evidence taxonomy (`Observed`, `ExperimentallyValidated`, `Inferred`, `VendorClaim`).
5. Do not convert an inferred behavior into an observed fact without evidence.
6. New network code must use `AevrixSecureTransport`; direct protected `HttpClient` creation is rejected by policy.
7. Pin security-sensitive third-party inputs to immutable revisions/digests.
8. Do not call a build homologated without AVA evidence.

## Pull request expectations

A pull request should state:

- problem and scope;
- threat/safety impact;
- tests executed and their actual result;
- dependencies/licenses introduced;
- whether the change alters evidence, transport, device identity, installer or update trust boundaries.

## Licensing

By submitting a contribution, you agree that it is licensed under Apache-2.0 and that you have the right to contribute it.
