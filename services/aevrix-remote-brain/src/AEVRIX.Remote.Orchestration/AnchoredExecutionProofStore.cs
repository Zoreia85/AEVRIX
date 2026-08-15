namespace Aevrix.Remote.Orchestration;

/// <summary>
/// External monotonic authority for execution-ledger heads. A production implementation must live
/// outside the rollback domain of the encrypted snapshot and must provide compare-and-swap semantics.
/// An absent project anchor is treated as ExecutionProofHead.Empty only for the first transition.
/// </summary>
public interface IExecutionProofHeadAnchor
{
    Task<ExecutionProofHead?> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task AdvanceAsync(
        Guid projectId,
        ExecutionProofHead expectedPrevious,
        ExecutionProofHead next,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Execution-proof stores that can explicitly finish the narrow, safe recovery case where an
/// authenticated snapshot was persisted but the independent monotonic anchor CAS did not complete.
/// Normal Load remains fail-closed; callers must deliberately enter reconciliation.
/// </summary>
public interface IRecoverableExecutionProofStore : IExecutionProofStore
{
    Task<StoredExecutionProofSnapshot?> ReconcileAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adds rollback detection to an execution-proof store by binding its authenticated snapshot to a
/// monotonically advancing head held in an independent trust domain. Snapshot bytes are persisted
/// before the anchor advances. A crash in that narrow interval therefore fails closed on Load rather
/// than accepting an unauthenticated history position. ReconcileAsync can finish only that exact
/// one-head pending CAS; every other mismatch remains blocked as rollback, fork or concurrent drift.
/// </summary>
public sealed class AnchoredExecutionProofStore : IRecoverableExecutionProofStore
{
    private readonly IExecutionProofStore _inner;
    private readonly IExecutionProofHeadAnchor _anchor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AnchoredExecutionProofStore(
        IExecutionProofStore inner,
        IExecutionProofHeadAnchor anchor)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
    }

    public async Task<StoredExecutionProofSnapshot?> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(projectId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _inner.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
            var anchoredHead = await _anchor.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);

            if (snapshot is null && anchoredHead is null) return null;
            if (snapshot is null)
                throw new InvalidDataException("Execution proof head anchor exists without its authenticated snapshot.");
            if (anchoredHead is null)
                throw new InvalidDataException("Execution proof snapshot exists without an external monotonic head anchor.");

            ExecutionProofLedger.VerifySnapshot(snapshot.Records, anchoredHead);
            if (snapshot.Head != anchoredHead)
                throw new InvalidDataException("Execution proof snapshot head does not match the external monotonic anchor.");
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredExecutionProofSnapshot?> ReconcileAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(projectId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _inner.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
            var anchoredHead = await _anchor.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);

            if (snapshot is null && anchoredHead is null) return null;
            if (snapshot is null)
                throw new InvalidDataException("Execution proof head anchor exists without its authenticated snapshot.");

            ValidateSnapshotCandidate(projectId, snapshot);

            if (anchoredHead is not null && anchoredHead == snapshot.Head)
                return snapshot;

            var previous = PreviousHead(snapshot.Records);
            var effectiveAnchor = anchoredHead ?? ExecutionProofHead.Empty;
            if (effectiveAnchor != previous)
                throw new InvalidDataException(
                    "Execution proof reconciliation rejected a snapshot/anchor divergence that is not the exact pending predecessor CAS.");

            await _anchor.AdvanceAsync(projectId, previous, snapshot.Head, cancellationToken).ConfigureAwait(false);

            var confirmed = await _anchor.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (confirmed != snapshot.Head)
                throw new InvalidDataException("Execution proof reconciliation could not confirm the advanced external anchor.");

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        Guid projectId,
        IReadOnlyList<ExecutionProofRecord> records,
        ExecutionProofHead head,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(projectId);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(head);
        if (head.EntryCount <= 0)
            throw new InvalidDataException("Anchored execution proof persistence requires at least one ledger record.");
        if (records.Any(record => record.Event.ProjectId != projectId))
            throw new InvalidDataException("Anchored execution proof persistence rejects mixed-project snapshots.");
        ExecutionProofLedger.VerifySnapshot(records, head);

        var previous = PreviousHead(records);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var anchoredHead = await _anchor.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);

            // Idempotent retry after a successful anchor advancement: refresh/repair the encrypted
            // snapshot with exactly the same already-authorized history but never move the anchor.
            if (anchoredHead is not null && anchoredHead == head)
            {
                await _inner.SaveAsync(projectId, records, head, cancellationToken).ConfigureAwait(false);
                return;
            }

            var effectiveAnchor = anchoredHead ?? ExecutionProofHead.Empty;
            if (effectiveAnchor != previous)
                throw new InvalidOperationException(
                    "Execution proof head anchor does not equal the required predecessor; rollback, fork or concurrent advance detected.");

            // Write data first. If the process dies before CAS, Load fails closed because snapshot and
            // anchor disagree. ReconcileAsync or an exact retry can safely finish the transition.
            await _inner.SaveAsync(projectId, records, head, cancellationToken).ConfigureAwait(false);
            await _anchor.AdvanceAsync(projectId, previous, head, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateSnapshotCandidate(Guid projectId, StoredExecutionProofSnapshot snapshot)
    {
        if (snapshot.ProjectId != projectId)
            throw new InvalidDataException("Execution proof reconciliation received a snapshot for another project.");
        if (snapshot.Head.EntryCount <= 0 || snapshot.Records.Count == 0)
            throw new InvalidDataException("Execution proof reconciliation requires a non-empty authenticated snapshot.");
        if (snapshot.Records.Any(record => record.Event.ProjectId != projectId))
            throw new InvalidDataException("Execution proof reconciliation rejects mixed-project snapshots.");
        ExecutionProofLedger.VerifySnapshot(snapshot.Records, snapshot.Head);
    }

    private static ExecutionProofHead PreviousHead(IReadOnlyList<ExecutionProofRecord> records)
    {
        if (records.Count == 1) return ExecutionProofHead.Empty;
        var predecessor = records[^2];
        return new ExecutionProofHead(predecessor.Sequence, predecessor.RecordHashSha256);
    }

    private static void ValidateProject(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
    }
}
