# AEVRIX Public Sanitation — Branch Batch 01

Authority: #494, #498, PR #499. This is classification only; no branch removal is claimed.

## Observed candidates

### tmp
- `tmp/desktop-patch-base-c442` — `ARCHIVE_EVIDENCE / RETIREMENT_CANDIDATE_PENDING_TREE_SCAN`. No common commit ancestor with current canonical main because of privacy-safe rewrites. Top-level snapshot differs from current main (`.github`, README, apps, docs, services), so name alone is insufficient for removal.

### validation
- `validation/ai-budget-base-1e744806`
- `validation/ai-budget-base-2cf6a56c`
- `validation/job-object-base-d0b6`
- `validation/job-object-direct-child-head-da9730c`
- `validation/job-object-fix-head-1b80c4d`
- `validation/job-object-head-fdf9657`
- `validation/job-object-nonblocking-head-72bf61a`
- `validation/native-windows-2a53bb6`
- `validation/research-browser-ephemeral`

Initial state for all validation refs: `ARCHIVE_EVIDENCE / RETIREMENT_CANDIDATE_PENDING_TREE_AND_REFERENCE_SCAN`. Exact historical result/hash should be retained before physical retirement.

### diagnostic
- `diagnostic/desktop-deployment-matrix-v1`
- `diagnostic/desktop-publish-flag-matrix-v1`
- `diagnostic/installer-hybrid-runtime-v1`
- `diagnostic/installer-self-contained-startup-v1`
- `diagnostic/post-first-run-mainwindow-v1`
- `diagnostic/wasdk-msix-direct-install-v1`
- `diagnostic/wasdk-runtime-nuget-v1`

Initial state for all diagnostic refs: `ARCHIVE_EVIDENCE / RETIREMENT_CANDIDATE_PENDING_OUTCOME_AND_REFERENCE_SCAN`.

## Required proof before retirement
1. identify any issue/PR/run that depends on the branch;
2. capture exact historical evidence that remains useful;
3. compare tree/content against current canonical state or proved successor;
4. prove no active workflow/runtime/reference requires the ref;
5. only then authorize physical ref removal when delete-ref access exists.
