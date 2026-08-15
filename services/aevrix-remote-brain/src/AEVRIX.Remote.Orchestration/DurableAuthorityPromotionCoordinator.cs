namespace Aevrix.Remote.Orchestration;

public sealed record DurablePromotionResult(
    string OperationId,
    string PromotionReference,
    ExecutionProofRecord CommitRecord,
    PromotionRecoveryRecord RecoveryRecord);

/// <summary>
/// Crash-recoverable promotion protocol. This is deliberately not described as a distributed
/// transaction. Safety comes from an Authority-backed authorization, a keyed durable recovery
/// journal, an externally idempotent/queryable executor, and explicit snapshot/anchor reconciliation.
/// </summary>
public sealed class DurableAuthorityPromotionCoordinator
{
    private readonly AuthorityBackedPromotionCommitGate _commitGate;
    private readonly IRecoverableExecutionProofStore _proofStore;
    private readonly IPromotionRecoveryJournal _journal;
    private readonly IRecoverablePromotionExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DurableAuthorityPromotionCoordinator(
        AuthorityBackedPromotionCommitGate commitGate,
        IRecoverableExecutionProofStore proofStore,
        IPromotionRecoveryJournal journal,
        IRecoverablePromotionExecutor executor,
        TimeProvider? timeProvider = null)
    {
        _commitGate = commitGate ?? throw new ArgumentNullException(nameof(commitGate));
        _proofStore = proofStore ?? throw new ArgumentNullException(nameof(proofStore));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DurablePromotionResult> CommitAsync(
        Guid projectId,
        string executionId,
        string commitEventId,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        ExecutionProofEvent.ValidateSafeId(executionId, nameof(executionId), 3, 160);
        ExecutionProofEvent.ValidateSafeId(commitEventId, nameof(commitEventId), 3, 160);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _proofStore.ReconcileAsync(projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Promotion requires an existing authorized execution-proof snapshot.");
            if (snapshot.ProjectId != projectId)
                throw new InvalidDataException("Recovered execution-proof snapshot belongs to another project.");

            var ledger = Rehydrate(snapshot);
            var evidence = BuildAuthorizationEvidence(ledger, executionId);
            var operationId = _journal.ComputeOperationId(evidence);
            var recovery = await _journal.PrepareAsync(
                evidence,
                commitEventId,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);

            var existingCommit = FindCommit(ledger, executionId);
            if (existingCommit is not null)
            {
                EnsureCommittedIdentity(existingCommit, recovery);
                recovery = await EnsureExecutorAppliedAsync(
                    recovery, evidence, attestation: null, allowExecution: false, cancellationToken).ConfigureAwait(false);
                recovery = await _journal.MarkLedgerCommittedAsync(
                    operationId,
                    existingCommit.RecordHashSha256,
                    existingCommit.Event.ObservedAt,
                    cancellationToken).ConfigureAwait(false);
                return new DurablePromotionResult(
                    operationId,
                    RequirePromotionReference(existingCommit.Event),
                    existingCommit,
                    recovery);
            }

            if (recovery.State == PromotionRecoveryState.LedgerCommitted)
                throw new InvalidDataException("Recovery journal claims LedgerCommitted but the anchored execution-proof snapshot has no promotion commit.");

            PromotionExecutionReceipt? executionReceipt = null;
            var committed = await _commitGate.CommitAsync(
                ledger,
                executionId,
                commitEventId,
                async (attestation, ct) =>
                {
                    recovery = await EnsureExecutorAppliedAsync(
                        recovery, evidence, attestation, allowExecution: true, ct).ConfigureAwait(false);
                    executionReceipt = await _executor.QueryAsync(operationId, ct).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Promotion executor lost an operation immediately after reporting it Applied.");
                    ValidateReceipt(executionReceipt, operationId);
                    return executionReceipt.PromotionReference;
                },
                cancellationToken).ConfigureAwait(false);

            executionReceipt ??= await _executor.QueryAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Promotion executor cannot confirm the just-committed external operation.");
            ValidateReceipt(executionReceipt, operationId);
            if (!string.Equals(committed.PromotionReference, executionReceipt.PromotionReference, StringComparison.Ordinal))
                throw new InvalidDataException("Execution-proof promotion reference does not match the idempotent executor receipt.");

            await _proofStore.SaveAsync(
                projectId,
                ledger.Snapshot(),
                ledger.Head,
                cancellationToken).ConfigureAwait(false);

            recovery = await _journal.MarkLedgerCommittedAsync(
                operationId,
                committed.CommitRecord.RecordHashSha256,
                committed.CommitRecord.Event.ObservedAt,
                cancellationToken).ConfigureAwait(false);

            return new DurablePromotionResult(
                operationId,
                executionReceipt.PromotionReference,
                committed.CommitRecord,
                recovery);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PromotionRecoveryRecord> EnsureExecutorAppliedAsync(
        PromotionRecoveryRecord recovery,
        PromotionEvidenceEnvelope evidence,
        PromotionAuthorityAttestation? attestation,
        bool allowExecution,
        CancellationToken cancellationToken)
    {
        var operationId = recovery.OperationId;
        var receipt = await _executor.QueryAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            ValidateReceipt(receipt, operationId);
            return await _journal.MarkAppliedAsync(operationId, receipt, cancellationToken).ConfigureAwait(false);
        }

        if (recovery.State is PromotionRecoveryState.Applied or PromotionRecoveryState.LedgerCommitted)
            throw new InvalidDataException(
                "Promotion journal records an applied external effect but the idempotent executor cannot confirm it; automatic replay is forbidden.");
        if (!allowExecution || attestation is null)
            throw new InvalidDataException(
                "Anchored ledger contains a promotion commit but the external executor cannot confirm the corresponding operation.");

        receipt = await _executor.ExecuteAsync(operationId, attestation, evidence, cancellationToken).ConfigureAwait(false);
        ValidateReceipt(receipt, operationId);
        return await _journal.MarkAppliedAsync(operationId, receipt, cancellationToken).ConfigureAwait(false);
    }

    private static ExecutionProofLedger Rehydrate(StoredExecutionProofSnapshot snapshot)
    {
        ExecutionProofLedger.VerifySnapshot(snapshot.Records, snapshot.Head);
        var ledger = new ExecutionProofLedger();
        foreach (var record in snapshot.Records)
        {
            var rebuilt = ledger.Append(record.Event);
            if (rebuilt.Sequence != record.Sequence
                || !FixedHashEquals(rebuilt.RecordHashSha256, record.RecordHashSha256))
                throw new InvalidDataException("Execution-proof snapshot could not be deterministically rehydrated.");
        }
        if (ledger.Head != snapshot.Head)
            throw new InvalidDataException("Rehydrated execution-proof ledger head does not match its authenticated snapshot.");
        return ledger;
    }

    private static PromotionEvidenceEnvelope BuildAuthorizationEvidence(ExecutionProofLedger ledger, string executionId)
    {
        var snapshot = ledger.Snapshot();
        ExecutionProofLedger.VerifySnapshot(snapshot, ledger.Head);
        var authorizationRecord = snapshot.SingleOrDefault(record =>
            string.Equals(record.Event.ExecutionId, executionId, StringComparison.Ordinal)
            && record.Event.Stage == ExecutionProofStage.PromotionAuthorized)
            ?? throw new InvalidOperationException("Execution has no promotion authorization record.");
        var authorization = authorizationRecord.Event;
        var evidence = new PromotionEvidenceEnvelope(
            ExecutionProofLedger.CurrentVersion,
            authorization.ProjectId,
            authorization.RunId,
            authorization.ExecutionId,
            authorization.CapabilityClass,
            authorization.CapabilityId,
            RequireHash(authorization.ArtifactManifestSha256, "artifact manifest"),
            RequireHash(authorization.ValidationDigestSha256, "validation digest"),
            RequireHash(authorization.JudgeDecisionDigestSha256, "Judge decision digest"),
            RequireHash(authorization.PromotionDigestSha256, "promotion digest"),
            authorizationRecord.RecordHashSha256,
            new ExecutionProofHead(authorizationRecord.Sequence, authorizationRecord.RecordHashSha256));

        // Before a commit exists, the existing Authority-backed gate must derive the exact same evidence.
        var commitExists = snapshot.Any(record =>
            string.Equals(record.Event.ExecutionId, executionId, StringComparison.Ordinal)
            && record.Event.Stage == ExecutionProofStage.PromotionCommitted);
        if (!commitExists)
        {
            var gateEvidence = ledger.BuildPromotionEvidence(executionId);
            if (!FixedHashEquals(gateEvidence.ComputeDigestSha256(), evidence.ComputeDigestSha256()))
                throw new InvalidDataException("Authorization evidence reconstruction disagrees with the Authority-backed promotion gate.");
        }

        return evidence;
    }

    private static ExecutionProofRecord? FindCommit(ExecutionProofLedger ledger, string executionId)
    {
        var matches = ledger.Snapshot().Where(record =>
            string.Equals(record.Event.ExecutionId, executionId, StringComparison.Ordinal)
            && record.Event.Stage == ExecutionProofStage.PromotionCommitted).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Execution-proof ledger contains multiple promotion commits for one execution.");
        return matches.SingleOrDefault();
    }

    private static void EnsureCommittedIdentity(ExecutionProofRecord commit, PromotionRecoveryRecord recovery)
    {
        if (!string.Equals(commit.Event.EventId, recovery.CommitEventId, StringComparison.Ordinal))
            throw new InvalidDataException("Recovered promotion commit event id does not match the durable recovery journal.");
        if (commit.Sequence != recovery.AuthorizedHeadEntryCount + 1)
            throw new InvalidDataException("Recovered promotion commit is not the direct successor of the authorized recovery head.");
        if (!FixedHashEquals(commit.PreviousRecordHashSha256, recovery.AuthorizedHeadHashSha256))
            throw new InvalidDataException("Recovered promotion commit predecessor does not match the durable authorized head.");
    }

    private static string RequirePromotionReference(ExecutionProofEvent item)
    {
        if (item.PromotionReference is null)
            throw new InvalidDataException("PromotionCommitted record is missing its promotion reference.");
        ExecutionProofEvent.ValidateSafeId(item.PromotionReference, nameof(item.PromotionReference), 3, 160);
        return item.PromotionReference;
    }

    private static string RequireHash(string? value, string name)
    {
        ExecutionProofEvent.ValidateSha256(value, name, required: true);
        return value!;
    }

    private static void ValidateReceipt(PromotionExecutionReceipt receipt, string expectedOperationId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.Equals(receipt.OperationId, expectedOperationId, StringComparison.Ordinal))
            throw new InvalidDataException("Promotion executor receipt is cross-bound to another operation id.");
        ExecutionProofEvent.ValidateSafeId(receipt.PromotionReference, nameof(receipt.PromotionReference), 3, 160);
        if (receipt.AppliedAt == default) throw new InvalidDataException("Promotion executor receipt timestamp is missing.");
    }

    private static bool FixedHashEquals(string left, string right)
    {
        ExecutionProofEvent.ValidateSha256(left, nameof(left), required: true);
        ExecutionProofEvent.ValidateSha256(right, nameof(right), required: true);
        var a = Convert.FromHexString(left);
        var b = Convert.FromHexString(right);
        try { return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b); }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(a);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(b);
        }
    }
}
