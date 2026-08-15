# Open-source model

AEVRIX source code, architecture, CI, manifests and release tooling are intended to be public.

Open source does **not** mean that operational secrets or third-party private data belong in Git.

## Public

- Windows/mobile source;
- remote-service source;
- protocol definitions;
- security design;
- deterministic test fixtures;
- CI workflows;
- release manifests/hashes/SBOM;
- documentation.

## Never public by default

- production private keys;
- code-signing private keys;
- CA private keys;
- API credentials;
- device private keys;
- target usernames/passwords/tokens/cookies;
- private research evidence;
- customer/project data;
- confidential infrastructure credentials.

Security must come from cryptography, authorization and compartmentalization — not from hiding client source code.
