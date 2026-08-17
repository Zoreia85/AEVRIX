# AEVRIX Public Freeze and Private Cutover

Status: **PUBLIC REPOSITORY FROZEN FOR NEW PRODUCT DEVELOPMENT**

Canonical operational destination: `Zoreia85/AEVRIX-Black-Core` (private).

## Purpose

This document records the repository boundary during the AEVRIX private cutover. It does not attempt to make already-published Apache-2.0 material private retroactively.

## Public repository role

`Zoreia85/AEVRIX` is a legacy/public-origin source and migration reference during cutover.

Allowed changes are limited to:

- cutover and migration notices;
- publication/IP-boundary safeguards;
- evidence needed to prove migration or independence;
- sanitation of stale branches, pull requests and obsolete public-only coordination;
- critical fixes required to prevent disclosure or unsafe migration.

New product features, proprietary architecture evolution, new reasoning/planning algorithms, advanced QIR learning, provider-arbitration heuristics, reconstruction strategy and other crown-jewel implementation must not be developed here.

## Private repository role

New operational AEVRIX development belongs in `Zoreia85/AEVRIX-Black-Core`.

Private cutover is not complete until the private candidate proves, on exact hashes, that it contains the required product/runtime/test/tooling inventory and no build, runtime, installer or CI path depends on cloning, downloading or reading this public repository.

Already-public code imported into the private repository remains public-origin and must preserve applicable provenance and license obligations.

## Fail-closed rule

If the private canonical repository is unavailable to an automation or developer session, that session must not continue feature development in the public repository as a fallback. It may perform only safe public sanitation, cutover documentation and boundary enforcement.

## Public IP boundary

Source Policy must reject:

- mutation of explicitly frozen disclosed crown-jewel compatibility files unless an approved migration updates the freeze inventory;
- private source/material path classes;
- model weights/checkpoints and other private runtime artifacts prohibited by the public-repository policy.

The public IP-boundary gate is a disclosure-prevention control, not proof that the private platform is complete.

## Cutover completion

`PUBLIC_DELETE_APPROVED` or equivalent destructive/public-retirement action is forbidden until `PRIVATE_CANONICAL_PASS` is supported by reproducible evidence for the exact private candidate, including the applicable build, runtime, security, installer lifecycle, recovery, isolation, product and migration-independence gates.

Until then:

- public state remains a migration source and audit record;
- private migration status remains evidence-driven;
- `HOMOLOGATED`, `100%` and deletion approval must not be inferred from documentation alone.
