# Trusted Knowledge → Blueprint Gate

AEVRIX keeps remote reasoning and platform-specific reconstruction models decoupled. The remote brain exports a neutral `BlueprintKnowledgeRequirement`; platform adapters may translate that contract into `ProjectBlueprint` elements without giving the Windows/Android/Apple clients authority to bypass Judge trust decisions.

Promotion rules:

- `Trusted` + `Convergent` → `Reconstructable` requirement.
- `Validated` + `Convergent` → `Conditional` requirement.
- `Candidate` or `Rejected` → blocked.
- `Contested` or `Insufficient` fusion → blocked.
- an explicit validation record is mandatory.
- every cited evidence id must exist in the same project Evidence Bus and match the same target and claim key.
- evidence basis is conservative: `VendorClaim` < `Inferred` < `Observed` < `ExperimentallyValidated`; mixed evidence uses the weakest applicable basis.
- sensitivity is propagated using the most restrictive contributing evidence classification.

The contract preserves the source knowledge id, validation record id and original evidence ids so downstream Reconstruction Studio adapters can keep end-to-end provenance. It contains no credentials, tokens, browser sessions or raw secret material.

This gate does not itself create UI/API/architecture models. It prevents untrusted or contested reasoning from being represented downstream as reconstruction fact.
