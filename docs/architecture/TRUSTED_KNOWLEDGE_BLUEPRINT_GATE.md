# Trusted Knowledge → Blueprint Gate

AEVRIX keeps remote reasoning and platform-specific reconstruction models decoupled. The remote brain exports a neutral `BlueprintKnowledgeRequirement`; platform adapters may translate that contract into `ProjectBlueprint` elements without giving the Windows/Android/Apple clients authority to bypass Judge trust decisions.

Promotion rules:

- the supplied mission object is not authoritative; the projector reloads the knowledge record from `ICandidateKnowledgeRepository` by knowledge id;
- supplied project/target identity must match that authoritative record;
- `Trusted` + independently re-derived `Convergent` → `Reconstructable` requirement;
- `Validated` + independently re-derived `Convergent` → `Conditional` requirement;
- `Candidate` or `Rejected` → blocked;
- `Contested` or `Insufficient` fusion → blocked;
- the caller-supplied fusion state must equal a fresh `EvidenceFusionEngine` result over the authoritative evidence set;
- an explicit validation record is mandatory;
- every cited evidence id must exist in the same project Evidence Bus and match the same target and claim key;
- evidence marked `PersonalData` or `ContainsPersonalData` must first be sanitized into a non-PII observation before Blueprint promotion;
- requirement confidence is capped by the independently recalculated fusion confidence;
- evidence basis is conservative: `VendorClaim` < `Inferred` < `Observed` < `ExperimentallyValidated`; mixed evidence uses the weakest applicable basis;
- sensitivity is propagated using the most restrictive contributing non-PII evidence classification.

Malformed knowledge envelopes are rejected before evidence lookup. The contract preserves the source knowledge id, validation record id and original evidence ids so downstream Reconstruction Studio adapters can keep end-to-end provenance. It contains no credentials, tokens, browser sessions or raw secret material.

This gate does not itself create UI/API/architecture models. It prevents untrusted, stale, forged, contested or unsanitized personal-data-bearing reasoning from being represented downstream as reconstruction fact.
