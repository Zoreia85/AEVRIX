# Build/QA handoff — exact privacy-root evidence

Desktop has completed its local diagnosis of the promotion conflict. Build/QA should own the corrective mechanism because it changes repository-wide CI/promotion behavior.

Handoff requirement:

- keep privacy-safe orphan-root promotion;
- test the exact bot candidate tree/SHA before canonical credit;
- preserve deterministic tree content and report the bot SHA/tree in evidence;
- prevent concurrent tracks from silently discarding one another during root replacement;
- do not require Desktop to force-push or weaken privacy rules.

Desktop will continue developing and validating stacked feature candidates until this repository-level promotion gate is repaired.
