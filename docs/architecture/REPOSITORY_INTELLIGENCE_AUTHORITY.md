# Repository Intelligence Authority

`docs/manifests/repository-intelligence.json` is the single canonical governance authority for external repositories used or studied by AEVRIX.

The C# `RepositoryIntelligenceCatalog` is a fail-closed bootstrap catalog, not a second approval database. Its static metadata is only a local compatibility/security hint; time-sensitive revision, pin, license-scope and runtime decisions come from the audited manifest. Bootstrap records cannot grant runtime execution even if other fields are modified to look approved.

Runtime execution requires an `AuditedManifest` record with manifest decision `Approved`, plus pin, SHA-256, verified license, independent security review and runtime allowlisting. `observedRevision` and `pinnedRevision` are separate: upstream HEAD drift never rewrites a pin. Multiple integration modes remain explicit, and any discovery/blocked mode keeps the decision fail-closed rather than being collapsed into an executable permission.

Core regression tests compare the canonical repository set and executable-license status with the bootstrap catalog and separately preserve critical Ollama/Scrapling/mxc/free-for-dev denials.
