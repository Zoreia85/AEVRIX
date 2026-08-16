# Exact privacy-root gate requirement

Status: BLOCKING GOVERNANCE OBSERVATION
Observed: 2026-08-16

The privacy-safe rewrite workflow intentionally creates a bot-authored orphan root and force-pushes it to `main`. GitHub does not automatically trigger a new workflow chain from that `GITHUB_TOKEN` push, so the resulting canonical bot SHA can exist without a `.NET core` run attached to that exact SHA.

This is not evidence that the bot root is unsafe; it is an evidence gap.

Required invariant before canonical readiness credit:

- the exact tree that becomes the bot root must run Source Policy and applicable Windows/.NET gates before or as part of deterministic promotion;
- the tested bot SHA/tree hash must be recorded in the promotion evidence;
- a predecessor user-authored SHA is insufficient by itself for exact-candidate credit, even if the privacy workflow copied its tree byte-for-byte;
- privacy-safe bot/noreply authorship remains mandatory;
- no solution may silently disable privacy rewriting or reintroduce personal author metadata.

Desktop completion tracker #290 must therefore keep canonical points fail-closed until exact-root evidence is available.
