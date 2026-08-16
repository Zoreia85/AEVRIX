# AEVRIX Product Surfaces and UX System

Status: design specification. This document does not claim release homologation.

## Product model

AEVRIX is one governed technical-intelligence platform exposed through three coordinated product surfaces:

1. **AEVRIX Desktop for Windows** — primary analysis workstation and local research console.
2. **AEVRIX Developer Platform** — authenticated API/SDK surface for automation, CI/CD and enterprise integration.
3. **AEVRIX Mobile Console** — Android/iOS companion for secure monitoring, approvals, uploads and result review.

All three surfaces use the same project/workspace identity, evidence provenance, authorization model and remote orchestration contracts. They are clients of one governed platform, not independent products.

## UX principles

- Technical, calm and high-trust rather than theatrical.
- Dark-first visual system with a light theme planned as an accessibility peer, not a degraded fallback.
- Critical states are explicit: Connected, Restricted, Offline, Degraded, Awaiting Approval, Failed Closed.
- Evidence provenance is visible in the UI. The interface must never blur Observed, ExperimentallyValidated, Inferred and VendorClaim.
- Destructive or authority-changing actions require explicit confirmation and explain their effect.
- Personal identifiers, credentials, raw secrets and workspace filesystem paths are never shown unless the user deliberately opens an authorized diagnostic surface.
- The UI must remain useful at 100%, 125%, 150% and 200% Windows scaling and support keyboard-first navigation.

## Visual language

The visual identity is based on a precise technical aesthetic: graphite/black surfaces, restrained luminous cyan/blue accents, glass-like depth used only where it improves hierarchy, fine grid geometry, high-information-density panels and generous spacing around primary decisions. Decorative effects must never reduce contrast or imply states that are not real.

Typography should use the native Windows UI stack for product controls and a monospaced technical face only for hashes, code, protocol events and evidence identifiers.

## Desktop information architecture

Primary navigation:

- Home / Command Center
- Projects
- New Investigation
- Mission Control
- Evidence
- Blueprint
- Research Browser
- Specialists / Adapters
- Activity / Proof Ledger
- Integrations
- Settings / Security

### Installer sequence

1. Welcome
2. System readiness check
3. Installation scope and destination
4. Security/runtime components
5. Privacy and data-boundary summary
6. Install progress
7. First-launch enrollment
8. Completion / launch

Installer screens must clearly distinguish components that are local from services that require network access. The installer must never imply that signing, backend enrollment or online capabilities succeeded before they have actually completed.

### Desktop first-run sequence

1. Splash / integrity check
2. Device enrollment
3. Account authentication
4. Workspace bootstrap
5. Security posture summary
6. Home / Command Center

### Command Center

The Command Center is the first operational screen. It shows active projects, engine health, remote-brain status, pending approvals, recent evidence, current missions and system security posture. It must provide one obvious action: **Start a new authorized analysis**.

### New Investigation wizard

Steps:

1. Scope and authorization class
2. Project/workspace
3. Target type and artifact/source selection
4. Analysis goals
5. Specialist/adapters plan
6. Data-sensitivity policy
7. Review and start

The wizard must explain why deep runtime instrumentation is unavailable for third-party clean-room targets when policy forbids it.

### Mission Control

Mission Control visualizes concurrent specialists as governed workers, not as uncontrolled agents. It includes dependency graph, queues, progress, resource use, evidence produced, current confidence and fail-closed blocks.

### Evidence

Evidence is displayed as a traceable object with source task, specialist, execution identity, sensitivity, cryptographic verification state and link to the execution-proof ledger. Raw artifacts and derived findings are visually distinct.

### Blueprint

Blueprint views include architecture graph, behavior flows, component map, knowledge requirements, confidence, unresolved gaps and proof-bound provenance. Export actions require admissible proof-bound knowledge.

## Developer Platform

The Developer Platform consists of an authenticated REST/streaming API plus SDKs. The UI for developers is a web-style console or embedded documentation surface containing:

- API keys/service identities managed outside source control;
- device/service enrollment;
- workspaces and projects;
- jobs and mission status;
- evidence and Blueprint retrieval;
- webhooks/event subscriptions when supported;
- rate limits and quotas;
- audit/proof records;
- SDK examples generated from the public API contract.

The external API never exposes direct unrestricted local filesystem access or bypasses the same authorization/provenance gates used by Desktop.

## Mobile Console

The mobile client is a secure companion rather than a full replacement for the Windows research engine. Primary areas:

- Home status
- Projects
- Mission progress
- Approval inbox
- Evidence preview
- Blueprint summary
- Secure upload/capture
- Notifications
- Account/device security

High-risk operations that require a desktop sandbox or Windows-native isolation must remain remote and explicit.

## Marketing surfaces in the public repository

The GitHub presentation should eventually include:

- polished hero banner;
- product positioning in one sentence;
- three-surface diagram (Desktop / API / Mobile);
- feature overview with only implemented or clearly marked planned capabilities;
- security and privacy principles;
- architecture diagram;
- product screenshots/mockups marked as concept until implemented;
- release status with no ambiguous homologation language;
- quick-start path once signed release artifacts exist;
- roadmap and contribution links.

Marketing material must never claim capabilities, certifications, security guarantees or release readiness that have not been demonstrated by evidence.

## Screen delivery order

Design and implementation should proceed in this order:

1. Windows installer — Welcome
2. Windows installer — Readiness / security
3. Windows installer — Components / destination
4. Windows installer — Progress
5. Windows installer — Completed / first launch
6. Desktop — Splash / integrity
7. Desktop — Sign in / enrollment
8. Desktop — Command Center
9. Desktop — New Investigation
10. Desktop — Mission Control
11. Desktop — Evidence Explorer
12. Desktop — Blueprint
13. Desktop — Integrations
14. Desktop — Security / Settings
15. Developer Platform — Overview
16. Developer Platform — API jobs / evidence
17. Mobile — Home
18. Mobile — Mission
19. Mobile — Approval
20. Mobile — Evidence / Blueprint

Each final screen must have an implementation counterpart and automated accessibility/layout checks before it is treated as production UI.