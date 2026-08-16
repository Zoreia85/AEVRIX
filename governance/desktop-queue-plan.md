# Desktop bot queue promotion order

Promotion is decomposed to coexist with active AEVRIX automation patches.

Order:
1. DesktopEngineSession.cs — authenticated `engine_ready` adapter, no UI mutation.
2. DesktopFirstRunService.cs — TPM-only first-run metadata/service, no remote enrollment claim.
3. DesktopProjectCatalogService.cs — read-only ProjectRepository adapter.
4. DesktopEvidenceExplorerService.cs — read-only EvidenceStore adapter and SHA-256 verification.
5. Desktop csproj + EngineHost readiness test.
6. Reconcile current bot `main` after existing Desktop health patch and then connect UI.

Each item must enter only through the authoritative `[AEVRIX-PATCH]` bot queue and be revalidated against the then-current `main`. No force-push and no reuse of stale PASS results.
