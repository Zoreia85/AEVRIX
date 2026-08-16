# AEVRIX GitHub Showcase, Installer and Visual Asset Pipeline

Status: product/publication specification. This document does not claim that final screenshots, signed installers or homologated release artifacts already exist.

## Goal

The public AEVRIX repository must operate as both a serious engineering repository and a high-quality product presentation surface. A visitor should understand the product, see the real software, understand its trust model and find the correct install/developer path without marketing claims outrunning evidence.

## README presentation order

The canonical README should evolve toward this order:

1. **Hero** — AEVRIX name, one-sentence positioning and current release status.
2. **Real product visual** — one wide production screenshot from an exact reproducible build.
3. **Three product surfaces** — Windows Desktop, Developer Platform and Mobile Console.
4. **60-second workflow** — scope -> governed mission -> specialist execution -> evidence -> Judge -> Blueprint.
5. **Screenshot gallery** — Command Center, Mission Control, Evidence, Blueprint, Research Browser and installer.
6. **Why AEVRIX is different** — evidence provenance, governed orchestration, isolated execution and reproducibility.
7. **Architecture** — concise diagram with link to full architecture docs.
8. **Security/privacy posture** — concise boundaries, not security theater.
9. **Install / Quick start** — visible only when an admissible downloadable artifact exists.
10. **Developer path** — SDK/API/contracts and contribution links.
11. **Release evidence** — status, exact hashes and homologation/AVA references.

The first viewport should communicate product identity and show the software itself rather than presenting a wall of technical text.

## Screenshot classes

Every public visual must belong to one of these classes:

- `PRODUCTION_CAPTURE` — captured from a real, reproducible AEVRIX build.
- `IMPLEMENTED_DEMO` — real implemented UI using deterministic demo/test data.
- `CONCEPT` — design direction that is not yet implemented.
- `DIAGRAM` — architecture or explanatory graphic.

A concept must display a visible `CONCEPT` label in the image or immediately adjacent caption. It must never be visually indistinguishable from an implemented product capture.

## Production screenshot provenance

A production screenshot is publishable only when metadata can bind it to the build that produced it.

Required metadata:

- AEVRIX version;
- Git commit SHA;
- executable/package SHA-256 when available;
- Windows version/build for Windows captures;
- display scale and viewport/resolution;
- capture timestamp;
- capture scenario/test-fixture id;
- screenshot SHA-256;
- classification (`PRODUCTION_CAPTURE` or `IMPLEMENTED_DEMO`);
- confirmation that secrets/PII/private project data are absent.

Recommended sidecar format:

```json
{
  "classification": "PRODUCTION_CAPTURE",
  "aevrixVersion": "0.x.y",
  "commit": "<git-sha>",
  "artifactSha256": "<sha256>",
  "scenario": "demo-command-center-001",
  "windowsBuild": "<build>",
  "displayScalePercent": 125,
  "viewport": "1920x1080",
  "capturedAtUtc": "<timestamp>",
  "screenshotSha256": "<sha256>",
  "containsSecrets": false,
  "containsPersonalData": false
}
```

## Proposed asset layout

```text
docs/assets/
  brand/
    hero/
    logos/
    icons/
  screenshots/
    windows/
      desktop/
      installer/
    developer/
    mobile/
  diagrams/
  demos/
  manifests/
```

Final production captures may add versioned subdirectories where that improves traceability.

## Windows screenshot set

The minimum public Windows gallery should eventually contain real captures of:

- installer welcome;
- installer readiness/security check;
- installer components/destination;
- installer progress;
- installer completed/first launch;
- device enrollment/sign-in;
- Command Center;
- New Investigation;
- Mission Control with multiple bounded specialists;
- Evidence Explorer;
- Blueprint;
- Research Browser;
- Integrations;
- Security / Settings;
- explicit restricted/degraded/fail-closed state.

The gallery should show meaningful states, not only empty dashboards.

## Installer visual system

The installer must look like the same product as the application. It should use the AEVRIX visual system without hiding native Windows trust information.

### Design rules

- dark-first AEVRIX visual identity with accessible contrast;
- product mark and concise purpose on the welcome screen;
- persistent step/progress context without excessive wizard chrome;
- readiness results expressed as `Ready`, `Warning`, `Blocked` or `Requires action`;
- runtime/security components explained in plain technical language;
- local versus network-dependent components clearly distinguished;
- installation destination and disk impact visible before commit;
- no fake success state for device enrollment, backend connection or signing;
- final screen shows exactly what was installed and the next action;
- repair, upgrade and uninstall experiences receive the same visual quality as first install.

### Installer sequence

1. Welcome
2. System readiness check
3. Scope and destination
4. Runtime/security components
5. Privacy/data-boundary summary
6. Installation progress
7. First-launch enrollment
8. Completion / launch

## Publicity and product communication

AEVRIX marketing should be visually strong but proof-led. The repository may use polished product language, diagrams, screenshots, short animated demos and comparison tables, but every technical claim must map to an implemented capability or a clearly marked roadmap item.

Preferred message hierarchy:

```text
What it is
  -> what problem it solves
  -> show it working
  -> explain why results are trustworthy
  -> explain architecture/security
  -> provide install/developer path
```

Avoid:

- star-count-based technology selection;
- unverifiable superlatives;
- "AI agents working autonomously" language that hides governance boundaries;
- quantum branding without a measured quantum/hybrid benchmark;
- claiming `HOMOLOGATED`, signed, secure or production-ready without the exact release evidence.

## Demo data policy

Public screenshots and demos must use deterministic synthetic/demo projects. They must not include:

- customer data;
- private repository names/paths;
- email addresses or personal identifiers;
- API keys, tokens, credentials or certificates;
- private evidence;
- filesystem paths that expose a real user's profile;
- internal service endpoints that are not intended to be public.

## Automated capture target

The target pipeline is:

```text
exact Windows build
  -> deterministic demo fixture
  -> launch exact artifact
  -> drive approved UI scenario
  -> capture selected screens
  -> secret/PII/static checks
  -> image hash + metadata manifest
  -> visual regression comparison
  -> human/AVA approval when required
  -> publish only approved captures
```

The capture workflow must use the same exact artifact that is being represented. A screenshot generated from a subsequent rebuild cannot be used as evidence for an earlier release hash.

## Visual regression

Production UI changes should eventually have baseline comparison at the supported Windows scale factors:

- 100%
- 125%
- 150%
- 200%

At minimum the regression gate should detect:

- clipping;
- overlapping controls;
- unreadable contrast;
- missing icons/assets;
- broken loading/empty/error states;
- incorrect branding;
- missing security/release-state indicators;
- screenshot changes that were not intentionally approved.

## GitHub publication gate for visuals

A visual can be promoted into the README hero/gallery only when:

1. classification is declared;
2. its source artifact or concept source is known;
3. secret/privacy inspection has passed;
4. the capture is current enough to represent the documented product surface;
5. any production claim shown in the visual is supported by the corresponding build;
6. the README caption accurately labels the visual;
7. obsolete captures are removed rather than accumulated indefinitely.

## Execution order

1. Create canonical `docs/assets` tree and manifests.
2. Produce the installer visual assets/spec against the current AEVRIX visual system.
3. Implement deterministic demo fixtures for safe public captures.
4. Capture real Windows application screens from CI/AVA-capable Windows execution.
5. Add screenshot manifest/hashes.
6. Add visual regression baseline.
7. Recompose the README first viewport around the real product visual.
8. Add Developer Platform and Mobile real captures when those surfaces exist.
9. Replace every concept image as its production implementation becomes available.

## Definition of done

The GitHub showcase work is complete only when a new visitor can see real AEVRIX software near the top of the repository, distinguish concepts from implementation, identify release status, understand the governed workflow and navigate to a safe install/developer path without encountering unsupported claims.
