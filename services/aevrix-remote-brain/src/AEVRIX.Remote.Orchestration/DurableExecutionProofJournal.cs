namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Project-bound transactional façade over <see cref="ExecutionProofLedger"/> and
/// <see cref="IExecutionProofStore"/>. An event becomes canonical in memory only after the exact
/// resulting snapshot has been durably accepted by the configured store. If persistence fails,
/// the candidate snapshot is retained for exact idempotent recovery and all different mutations
/// are blocked until that recovery succeeds.
/// </summary>
public sealed class DurableExecutionProofJournal
{
    private readonly Guid _projectId;
    private readonly IExecutionProofStore _store;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _stateSync = new();

    private ExecutionProofLedger _canonical;
    private PendingCandidate? _pending;

    private DurableExecutionProofJournal(
        Guid projectId,
        IExecutionProofStore store,
        ExecutionProofLedger canonical)
    {
        _projectId = projectId;
        _store = store;
        _canonical = canonical;
    }

    public Guid ProjectId => _projectId;

    public ExecutionProofHead Head
    {
        get
        {
            lock (_stateSync)
            {
                return _canonical.Head;
            }
        }
    }

    public bool HasPendingRecovery
    {
        get
        {
            lock (_stateSync)
            {
                return _pending is not null;
            }
        }
    }

    public static async Task<DurableExecutionProofJournal> OpenAsync(
        Guid projectId,
        IExecutionProofStore store,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(projectId);
        ArgumentNullException.ThrowIfNull(store);

        var snapshot = await store.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
        var ledger = snapshot is null
            ? new ExecutionProofLedger()
            : Rebuild(projectId, snapshot);
        return new DurableExecutionProofJournal(projectId, store, ledger);
    }

    /// <summary>
    /// Appends one semantically valid event to a copy of the canonical ledger and persists the
    /// resulting complete snapshot. The in-memory canonical state advances only after SaveAsync
    /// returns successfully. On failure the exact candidate is retained for recovery.
    /// </summary>
    public async Task<ExecutionProofRecord> AppendAndPersistAsync(
        ExecutionProofEvent item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ProjectId != _projectId)
            throw new InvalidDataException("Execution proof journal rejects an event from another project.");

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfRecoveryPending();

            ExecutionProofLedger candidate;
            lock (_stateSync)
            {
                candidate = Rebuild(_projectId, _canonical.Snapshot(), _canonical.Head);
            }

            var appended = candidate.Append(item);
            var records = candidate.Snapshot();
            var head = candidate.Head;
            var pending = new PendingCandidate(records.ToArray(), head);

            try
            {
                await _store.SaveAsync(_projectId, pending.Records, pending.Head, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                lock (_stateSync)
                {
                    _pending = pending;
                }
                throw;
            }

            lock (_stateSync)
            {
                _canonical = candidate;
            }
            return appended;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Retries only the exact failed candidate. This is intentionally not a generic retry of the
    /// original operation: stores such as <see cref="AnchoredExecutionProofStore"/> can complete
    /// their write-then-CAS crash interval only when the identical snapshot/head is submitted.
    /// </summary>
    public async Task<ExecutionProofHead> RecoverPendingAsync(
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingCandidate? pending;
            lock (_stateSync)
            {
                pending = _pending;
            }

            if (pending is null)
            {
                lock (_stateSync)
                {
                    return _canonical.Head;
                }
            }

            await _store.SaveAsync(_projectId, pending.Records, pending.Head, cancellationToken)
                .ConfigureAwait(false);

            var recovered = Rebuild(
                _projectId,
                new StoredExecutionProofSnapshot(_projectId, pending.Records, pending.Head));
            lock (_stateSync)
            {
                _canonical = recovered;
                _pending = null;
                return _canonical.Head;
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Reloads a store snapshot when there is no unresolved local write. The reload may be equal
    /// to or a strict extension of the current canonical chain, but it may never roll the journal
    /// backward or replace its existing prefix with a fork.
    /// </summary>
    public async Task<ExecutionProofHead> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfRecoveryPending();

            IReadOnlyList<ExecutionProofRecord> currentRecords;
            ExecutionProofHead currentHead;
            lock (_stateSync)
            {
                currentRecords = _canonical.Snapshot();
                currentHead = _canonical.Head;
            }

            var snapshot = await _store.LoadAsync(_projectId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                if (currentHead != ExecutionProofHead.Empty)
                    throw new InvalidDataException("Execution proof journal refresh detected disappearance of a canonical history.");
                return currentHead;
            }

            var refreshed = Rebuild(_projectId, snapshot);
            EnsureMonotonicExtension(currentRecords, currentHead, snapshot.Records, snapshot.Head);
            lock (_stateSync)
            {
                _canonical = refreshed;
                return _canonical.Head;
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public IReadOnlyList<ExecutionProofRecord> Snapshot()
    {
        lock (_stateSync)
        {
            return _canonical.Snapshot();
        }
    }

    /// <summary>
    /// Promotion evidence is built only from the last successfully persisted canonical chain.
    /// A failed candidate can therefore never authorize promotion before recovery succeeds.
    /// </summary>
    public PromotionEvidenceEnvelope BuildPromotionEvidence(string executionId)
    {
        lock (_stateSync)
        {
            return _canonical.BuildPromotionEvidence(executionId);
        }
    }

    private void ThrowIfRecoveryPending()
    {
        lock (_stateSync)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException(
                    "Execution proof journal has an unresolved persistence candidate; exact recovery is required before another mutation or refresh.");
            }
        }
    }

    private static ExecutionProofLedger Rebuild(
        Guid projectId,
        StoredExecutionProofSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ProjectId != projectId)
            throw new InvalidDataException("Execution proof store returned a snapshot for another project.");
        return Rebuild(projectId, snapshot.Records, snapshot.Head);
    }

    private static ExecutionProofLedger Rebuild(
        Guid projectId,
        IReadOnlyList<ExecutionProofRecord> records,
        ExecutionProofHead head)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(head);
        if (records.Any(record => record is null || record.Event.ProjectId != projectId))
            throw new InvalidDataException("Execution proof snapshot contains a null or cross-project record.");

        ExecutionProofLedger.VerifySnapshot(records, head);
        var rebuilt = new ExecutionProofLedger();
        foreach (var existing in records)
        {
            var replayed = rebuilt.Append(existing.Event);
            if (replayed != existing)
                throw new InvalidDataException("Execution proof snapshot could not be reproduced byte-for-byte from its events.");
        }

        if (rebuilt.Head != head)
            throw new InvalidDataException("Execution proof rebuilt head differs from the persisted head.");
        return rebuilt;
    }

    private static void EnsureMonotonicExtension(
        IReadOnlyList<ExecutionProofRecord> currentRecords,
        ExecutionProofHead currentHead,
        IReadOnlyList<ExecutionProofRecord> refreshedRecords,
        ExecutionProofHead refreshedHead)
    {
        if (refreshedHead.EntryCount < currentHead.EntryCount)
            throw new InvalidDataException("Execution proof journal refresh rejected a rollback.");

        if (currentHead == ExecutionProofHead.Empty)
            return;

        if (refreshedRecords.Count < currentRecords.Count)
            throw new InvalidDataException("Execution proof journal refresh rejected a truncated history.");

        for (var index = 0; index < currentRecords.Count; index++)
        {
            if (refreshedRecords[index] != currentRecords[index])
                throw new InvalidDataException("Execution proof journal refresh rejected a forked canonical prefix.");
        }

        if (refreshedHead.EntryCount == currentHead.EntryCount && refreshedHead != currentHead)
            throw new InvalidDataException("Execution proof journal refresh rejected a same-height fork.");
    }

    private static void ValidateProject(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
    }

    private sealed record PendingCandidate(
        ExecutionProofRecord[] Records,
        ExecutionProofHead Head);
}
