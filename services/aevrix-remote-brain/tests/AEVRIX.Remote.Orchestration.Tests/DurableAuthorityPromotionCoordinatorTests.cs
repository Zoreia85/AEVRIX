using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class DurableAuthorityPromotionCoordinatorTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 23, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task CommitAsync_HappyPath_ExecutesOncePersistsAndCommitsJournal()
    {
        using var temp = new TempDirectory();
        var ledger = AuthorizedLedger();
        var store = new MemoryRecoverableStore(Snapshot(ledger));
        using var journal = Journal(temp.Path);
        var executor = new MemoryExecutor();
        var coordinator = Coordinator(ledger.Head, store, journal, executor);

        var result = await coordinator.CommitAsync(Project, "exec-durable", "event-durable-commit");

        Assert.AreEqual(1, executor.ExecuteCount);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(PromotionRecoveryState.LedgerCommitted, result.RecoveryRecord.State);
        Assert.AreEqual(ExecutionProofStage.PromotionCommitted, result.CommitRecord.Event.Stage);
        Assert.AreEqual("commit:durable-001", result.PromotionReference);
        Assert.AreEqual(6, store.Current!.Head.EntryCount);
    }

    [TestMethod]
    public async Task CommitAsync_PreparedButEffectAlreadyExists_QueriesInsteadOfExecutingAgain()
    {
        using var temp = new TempDirectory();
        var ledger = AuthorizedLedger();
        var store = new MemoryRecoverableStore(Snapshot(ledger));
        using var journal = Journal(temp.Path);
        var evidence = ledger.BuildPromotionEvidence("exec-durable");
        var prepared = await journal.PrepareAsync(evidence, "event-durable-commit", Now);
        var executor = new MemoryExecutor();
        executor.Seed(new PromotionExecutionReceipt(prepared.OperationId, "commit:durable-001", Now.AddSeconds(1)));
        var coordinator = Coordinator(ledger.Head, store, journal, executor);

        var result = await coordinator.CommitAsync(Project, "exec-durable", "event-durable-commit");

        Assert.AreEqual(0, executor.ExecuteCount);
        Assert.IsTrue(executor.QueryCount >= 1);
        Assert.AreEqual(PromotionRecoveryState.LedgerCommitted, result.RecoveryRecord.State);
    }

    [TestMethod]
    public async Task CommitAsync_PersistenceFailsAfterExternalEffect_RetryDoesNotDuplicateEffect()
    {
        using var temp = new TempDirectory();
        var ledger = AuthorizedLedger();
        var store = new MemoryRecoverableStore(Snapshot(ledger)) { FailNextSave = true };
        using var journal = Journal(temp.Path);
        var executor = new MemoryExecutor();
        var coordinator = Coordinator(ledger.Head, store, journal, executor);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.CommitAsync(Project, "exec-durable", "event-durable-commit"));
        Assert.AreEqual(1, executor.ExecuteCount);
        Assert.AreEqual(5, store.Current!.Head.EntryCount);

        var recovered = await coordinator.CommitAsync(Project, "exec-durable", "event-durable-commit");

        Assert.AreEqual(1, executor.ExecuteCount, "retry must query the idempotent executor rather than apply the effect again");
        Assert.AreEqual(6, store.Current!.Head.EntryCount);
        Assert.AreEqual(PromotionRecoveryState.LedgerCommitted, recovered.RecoveryRecord.State);
    }

    [TestMethod]
    public async Task CommitAsync_JournalSaysAppliedButExecutorLostOperation_FailsClosedWithoutReplay()
    {
        using var temp = new TempDirectory();
        var ledger = AuthorizedLedger();
        var store = new MemoryRecoverableStore(Snapshot(ledger));
        using var journal = Journal(temp.Path);
        var evidence = ledger.BuildPromotionEvidence("exec-durable");
        var prepared = await journal.PrepareAsync(evidence, "event-durable-commit", Now);
        var receipt = new PromotionExecutionReceipt(prepared.OperationId, "commit:durable-001", Now.AddSeconds(1));
        await journal.MarkAppliedAsync(prepared.OperationId, receipt);
        var executor = new MemoryExecutor(); // intentionally empty
        var coordinator = Coordinator(ledger.Head, store, journal, executor);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            coordinator.CommitAsync(Project, "exec-durable", "event-durable-commit"));

        Assert.AreEqual(0, executor.ExecuteCount);
        Assert.AreEqual(5, store.Current!.Head.EntryCount);
    }

    [TestMethod]
    public async Task CommitAsync_CommitAlreadyAnchored_FinalizesJournalWithoutExecutingAgain()
    {
        using var temp = new TempDirectory();
        var ledger = AuthorizedLedger();
        using var journal = Journal(temp.Path);
        var evidence = ledger.BuildPromotionEvidence("exec-durable");
        var prepared = await journal.PrepareAsync(evidence, "event-durable-commit", Now);
        var receipt = new PromotionExecutionReceipt(prepared.OperationId, "commit:durable-001", Now.AddSeconds(1));
        await journal.MarkAppliedAsync(prepared.OperationId, receipt);
        ledger.Append(CommitEvent("commit:durable-001"));
        var store = new MemoryRecoverableStore(Snapshot(ledger));
        var executor = new MemoryExecutor();
        executor.Seed(receipt);
        var coordinator = Coordinator(new ExecutionProofHead(5, evidence.LedgerHead.HeadHashSha256), store, journal, executor);

        var result = await coordinator.CommitAsync(Project, "exec-durable", "event-durable-commit");

        Assert.AreEqual(0, executor.ExecuteCount);
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual(PromotionRecoveryState.LedgerCommitted, result.RecoveryRecord.State);
        Assert.AreEqual(ledger.Head.HeadHashSha256, result.CommitRecord.RecordHashSha256);
    }

    private static DurableAuthorityPromotionCoordinator Coordinator(
        ExecutionProofHead authorityHead,
        IRecoverableExecutionProofStore store,
        IPromotionRecoveryJournal journal,
        MemoryExecutor executor)
    {
        var authority = new FakeAuthority(authorityHead);
        var commitGate = new AuthorityBackedPromotionCommitGate(authority, new FixedTimeProvider(Now.AddMinutes(5)));
        return new DurableAuthorityPromotionCoordinator(
            commitGate,
            store,
            journal,
            executor,
            new FixedTimeProvider(Now));
    }

    private static FileBackedPromotionRecoveryJournal Journal(string path) => new(
        path,
        Enumerable.Repeat((byte)0x41, 32).ToArray(),
        Enumerable.Repeat((byte)0x52, 32).ToArray());

    private static ExecutionProofLedger AuthorizedLedger()
    {
        var ledger = new ExecutionProofLedger();
        ledger.Append(Event("start", ExecutionProofStage.Started, ExecutionProofOutcome.Pending));
        ledger.Append(Event("complete", ExecutionProofStage.Completed, ExecutionProofOutcome.Succeeded,
            result: H('c'), attestation: H('d'), artifact: H('e')));
        ledger.Append(Event("validation", ExecutionProofStage.ValidationCompleted, ExecutionProofOutcome.Succeeded,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f')));
        ledger.Append(Event("judge", ExecutionProofStage.JudgeDecided, ExecutionProofOutcome.Approved,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), judge: H('1')));
        ledger.Append(Event("authorize", ExecutionProofStage.PromotionAuthorized, ExecutionProofOutcome.Approved,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), judge: H('1'), promotion: H('2')));
        return ledger;
    }

    private static ExecutionProofEvent CommitEvent(string reference) =>
        Event("commit", ExecutionProofStage.PromotionCommitted, ExecutionProofOutcome.Committed,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), judge: H('1'), promotion: H('2'),
            promotionReference: reference, observedAt: Now.AddMinutes(5));

    private static ExecutionProofEvent Event(
        string id,
        ExecutionProofStage stage,
        ExecutionProofOutcome outcome,
        string? result = null,
        string? attestation = null,
        string? artifact = null,
        string? validation = null,
        string? judge = null,
        string? promotion = null,
        string? promotionReference = null,
        DateTimeOffset? observedAt = null) => new(
            "event-durable-" + id,
            Project,
            "run-durable",
            "exec-durable",
            stage,
            "coding-agent",
            "sandbox-worker",
            outcome,
            H('a'), H('b'), result, attestation, artifact, validation, judge, promotion, promotionReference,
            observedAt ?? Now.AddMinutes((int)stage));

    private static StoredExecutionProofSnapshot Snapshot(ExecutionProofLedger ledger) =>
        new(Project, ledger.Snapshot(), ledger.Head);

    private static string H(char value) => new(value, 64);

    private sealed class FakeAuthority(ExecutionProofHead head) : IExecutionPromotionAuthority
    {
        public Task<ExecutionProofHead?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExecutionProofHead?>(head);

        public Task AdvanceAsync(Guid projectId, ExecutionProofHead expectedPrevious, ExecutionProofHead next,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PromotionAuthorityAttestation> RequestPromotionAttestationAsync(
            PromotionEvidenceEnvelope evidence, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PromotionAuthorityAttestation(
                PromotionAuthorityAttestation.CurrentVersion,
                "authority-durable-key",
                evidence.ProjectId,
                evidence.RunId,
                evidence.ExecutionId,
                evidence.ComputeDigestSha256(),
                evidence.LedgerHead.EntryCount,
                evidence.LedgerHead.HeadHashSha256,
                Now.AddMinutes(-1).ToUnixTimeSeconds(),
                Now.AddMinutes(10).ToUnixTimeSeconds(),
                "0123456789abcdef0123456789abcdef",
                "AA==",
                H('8')));
    }

    private sealed class MemoryRecoverableStore(StoredExecutionProofSnapshot initial) : IRecoverableExecutionProofStore
    {
        public StoredExecutionProofSnapshot? Current { get; private set; } = initial;
        public int SaveCount { get; private set; }
        public bool FailNextSave { get; set; }

        public Task<StoredExecutionProofSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<StoredExecutionProofSnapshot?> ReconcileAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(Guid projectId, IReadOnlyList<ExecutionProofRecord> records, ExecutionProofHead head,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("synthetic post-effect persistence failure");
            }
            Current = new StoredExecutionProofSnapshot(projectId, records.ToArray(), head);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryExecutor : IRecoverablePromotionExecutor
    {
        private readonly Dictionary<string, PromotionExecutionReceipt> _receipts = new(StringComparer.Ordinal);
        public int QueryCount { get; private set; }
        public int ExecuteCount { get; private set; }

        public Task<PromotionExecutionReceipt?> QueryAsync(string operationId, CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return Task.FromResult(_receipts.TryGetValue(operationId, out var receipt) ? receipt : null);
        }

        public Task<PromotionExecutionReceipt> ExecuteAsync(
            string operationId,
            PromotionAuthorityAttestation attestation,
            PromotionEvidenceEnvelope evidence,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            if (_receipts.TryGetValue(operationId, out var existing)) return Task.FromResult(existing);
            var receipt = new PromotionExecutionReceipt(operationId, "commit:durable-001", Now.AddSeconds(1));
            _receipts[operationId] = receipt;
            return Task.FromResult(receipt);
        }

        public void Seed(PromotionExecutionReceipt receipt) => _receipts[receipt.OperationId] = receipt;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-durable-promotion", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
