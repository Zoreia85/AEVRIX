# AEVRIX — Public Sanitation Status

Authority: `Zoreia85/AEVRIX#494`. Public repo is the temporary operational line while private Actions capacity is unavailable. `AEVRIX-Black-Core` remains private/frozen.

Snapshot (2026-08-17 BRT): 251 branches including main; 4 PRs open before cleanup.

PR classifications: #496 QUARANTINE/KEEP_ACTIVE; #493 QUARANTINE/MERGE_CANDIDATE; #479 QUARANTINE/MERGE_CANDIDATE; #364 BLOCKED_UNTIL_PRIVATE/ARCHIVE_EVIDENCE (preserve branch/provenance, close without merge while public mode is active).

Historical presumptions: `tmp/*` TRASH_CANDIDATE; stale `validation/*` ARCHIVE_EVIDENCE/TRASH_CANDIDATE; superseded `diagnostic/*` ARCHIVE_EVIDENCE/TRASH_CANDIDATE; repeated version/replay/canonical/clean iterations retain only newest proven survivor after equivalence/reference scan; crown-jewel work BLOCKED_UNTIL_PRIVATE.

Promotion to main requires exact base/head SHA, one owner, exact tests, policy/security, regression/compatibility, explicit blockers/skips and S4 PASS. Missing/stale mandatory evidence, FAIL, mandatory SKIP, unknown provenance or IP-boundary violation => QUARANTINE.

Administrative note: this ledger was created through documentation-only direct main writes by the connected Contents API. Those commits are not functional promotions; exact public CI/policy must post-validate the resulting main before it is trusted. Functional code must not use this path.

Branch-ref deletion is not exposed by the current connector. Physical purge cannot be claimed until deletion-capable access is available.
