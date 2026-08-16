# Desktop / privacy-root rewrite coordination observation

Status: OPEN OBSERVATION
Scope owner: Build / QA governance, not Desktop runtime security
Observed: 2026-08-16

## Reproduction observed during Windows Desktop work

1. A Desktop branch is created from the then-current privacy-safe `main` root.
2. A pull request is opened and Windows/Source Policy checks begin.
3. An unrelated non-bot push reaches `main`.
4. `.github/workflows/privacy-root-rewrite.yml` replaces `main` with a bot-authored orphan root using a force push.
5. The open Desktop PR loses a stable ancestry/base and has repeatedly been closed without merge while its feature branch remains intact.

Observed examples in this cycle: PR #286 and PR #300 were closed without merge while Desktop feature branches continued to exist.

## Security/privacy invariant

Do **not** remove the requirement that canonical public history be privacy-safe and bot/noreply-authored. The problem is the coordination mechanism, not the privacy objective.

## Required build-control outcome

Provide a promotion path where:

- candidate branch content can be validated by exact SHA on Windows CI;
- privacy sanitization happens without silently discarding validated candidate tree content;
- canonical `main` receives the validated tree through a deterministic promotion operation;
- the resulting privacy-safe canonical root is revalidated by exact SHA;
- PR/issue state remains auditable even if ancestry is intentionally replaced;
- concurrent AEVRIX tracks cannot overwrite one another's promoted tree.

Until this is solved, Desktop work should validate through branch `push` workflows and must not use force updates to chase the moving canonical root.
