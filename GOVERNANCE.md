# AEVRIX Governance

AEVRIX is developed in public. Maintainers may accept community contributions, but release promotion remains evidence-driven.

## Decision hierarchy

1. Security and clean-room boundaries.
2. Reproducible evidence and validation.
3. Product architecture and compatibility.
4. UX/performance improvements.

No contributor, maintainer or automation can override a failed release gate by changing its label from FAIL/BLOCKED to PASS without new technical evidence.

## Release states

- `PASS` — gate executed successfully with evidence.
- `FAIL` — gate executed and failed.
- `PARCIAL` — only part of a gate was executed.
- `BLOQUEADO` — gate could not execute due to a concrete blocker.
- `INFRASTRUCTURE_INCONCLUSIVE` — infrastructure failed before the product could be meaningfully evaluated.

`HOMOLOGATED` is a release-level state and requires every mandatory gate in `docs/VALIDATION.md` to pass for the exact released hashes.
