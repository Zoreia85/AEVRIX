# Action requested from Build / QA

Implement a deterministic privacy-safe promotion gate that validates the exact bot-authored root candidate before it is treated as canonical evidence.

Acceptance criteria:

1. Candidate tree is reconciled against current canonical main.
2. Privacy-safe author rewrite remains mandatory.
3. Source Policy and all applicable product gates run against the exact tree that will become main.
4. The bot commit SHA and tree SHA are recorded in evidence.
5. Promotion aborts fail-closed on any failed/skipped mandatory gate.
6. Concurrent product tracks cannot silently overwrite each other's accepted tree content.
7. No personal author e-mail/name is reintroduced into canonical public history.
