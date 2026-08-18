# Branch Batch 01 — evidence ledger

Sanitation review, 2026-08-17 BRT. Classification only; no ref removal claimed.

## Job Object validation refs

- `validation/job-object-head-fdf9657` -> PR #65: validation-only; explicitly superseded; never merge. Classification: `ARCHIVE_EVIDENCE / RETIREMENT_CANDIDATE` after preserving PR/run evidence.
- `validation/job-object-fix-head-1b80c4d` -> PR #67: validation-only; explicitly superseded; never merge. Classification: `ARCHIVE_EVIDENCE / RETIREMENT_CANDIDATE` after preserving PR/run evidence.
- `validation/job-object-nonblocking-head-72bf61a` -> PR #70: validation purpose completed; later canonical runtime/security increments superseded it; must not merge. Classification: `ARCHIVE_EVIDENCE / RETIREMENT_CANDIDATE` after evidence capture.
- `validation/job-object-direct-child-head-da9730c` -> PR #72: validation-only; exact canonical SHA historically passed Source Policy and Windows/.NET test suites; closed without merge by design. Classification: `ARCHIVE_EVIDENCE`; retain the result record, branch can become retirement candidate after evidence snapshot.
- `validation/job-object-base-d0b6` -> no direct PR reference found in repository search. Classification remains `PENDING_REFERENCE_SCAN`.

## Other validation refs

- `validation/research-browser-ephemeral` -> PR #233: disposable validation branch, explicitly not intended for canonical merge; exact diff intended to be replayed through privacy-safe bot queue. Classification: `ARCHIVE_EVIDENCE / RETIREMENT_CANDIDATE` after proving replay/canonical successor exists.
- `validation/native-windows-2a53bb6` -> no direct PR reference found in repository search. `PENDING_REFERENCE_SCAN`.
- `validation/ai-budget-base-1e744806` -> no direct PR reference found in repository search. `PENDING_REFERENCE_SCAN`.
- `validation/ai-budget-base-2cf6a56c` -> no direct PR reference found in repository search. `PENDING_REFERENCE_SCAN`.

Privacy-safe main rewriting means commit ancestry alone is insufficient. Retirement approval requires content/tree or proved-successor evidence plus reference scan.
