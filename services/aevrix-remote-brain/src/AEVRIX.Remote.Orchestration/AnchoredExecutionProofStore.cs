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
/// Adds rollback detection to an execution-proof store by binding its authenticated snapshot to a
/// monotonically advancing head held in an independent trust domain. Snapshot bytes are persisted
/// before the anchor advances. A crash in that narrow interval therefore fails closed on Load rather
/// than accepting an unauthenticated history position; retrying the same Save can complete the CAS.
/// </summary>
public sealed class AnchoredExecutionProofStore : IExecutionProofStore
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
            // anchor disagree. A retry with the same candidate can safely finish the transition.
            await _inner.SaveAsync(projectId, records, head, cancellationToken).ConfigureAwait(false);
            await _anchor.AdvanceAsync(projectId, previous, head, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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
