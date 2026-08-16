using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class DurableExecutionProofJournalTests
{
    private static readonly Guid Project = Guid.Parse("51515151-5151-5151-5151-515151515151");

    [TestMethod]
    public async Task AppendAndPersist_AdvancesCanonicalStateOnlyAfterSaveSucceeds()
    {
        var store = new MemoryExecutionProofStore();
        var journal = await DurableExecutionProofJournal.OpenAsync(Project, store);

        var record = await journal.AppendAndPersistAsync(Started());

        Assert.AreEqual(1L, record.Sequence);
        Assert.AreEqual(1L, journal.Head.EntryCount);
        Assert.IsFalse(journal.HasPendingRecovery);
        var persisted = await store.LoadAsync(Project);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(journal.Head, persisted.Head);
        CollectionAssert.AreEqual(journal.Snapshot().ToArray(), persisted.Records.ToArray());
    }

    [TestMethod]
    public async Task Open_RebuildsPersistedSnapshotExactly()
    {
        var store = new MemoryExecutionProofStore();
        var seed = new ExecutionProofLedger();
        seed.Append(Started());
        seed.Append(Completed());
        await store.SaveAsync(Project, seed.Snapshot(), seed.Head);

        var journal = await DurableExecutionProofJournal.OpenAsync(Project, store);

        Assert.AreEqual(seed.Head, journal.Head);
        CollectionAssert.AreEqual(seed.Snapshot().ToArray(), journal.Snapshot().ToArray());
    }

    [TestMethod]
    public async Task FailedSave_DoesNotAdvanceCanonicalHead_AndBlocksDifferentMutation()
    {
        var inner = new MemoryExecutionProofStore();
        var store = new FailBeforePersistOnceStore(inner);
        var journal = await DurableExecutionProofJournal.OpenAsync(Project, store);

        await Assert.ThrowsExactlyAsync<IOException>(() => journal.AppendAndPersistAsync(Started()));

        Assert.AreEqual(ExecutionProofHead.Empty, journal.Head);
        Assert.AreEqual(0, journal.Snapshot().Count);
        Assert.IsTrue(journal.HasPendingRecovery);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            journal.AppendAndPersistAsync(Started() with { EventId = "event-start-other" }));
    }

    [TestMethod]
    public async Task RecoverPending_RetriesExactCandidateAndMakesItCanonical()
    {
        var inner = new MemoryExecutionProofStore();
        var store = new FailBeforePersistOnceStore(inner);
        var journal = await DurableExecutionProofJournal.OpenAsync(Project, store);

        await Assert.ThrowsExactlyAsync<IOException>(() => journal.AppendAndPersistAsync(Started()));
        var recoveredHead = await journal.RecoverPendingAsync();

        Assert.AreEqual(1L, recoveredHead.EntryCount);
        Assert.AreEqual(recoveredHead, journal.Head);
        Assert.IsFalse(journal.HasPendingRecovery);
        Assert.AreEqual(1, journal.Snapshot().Count);
        Assert.AreEqual("event-start", journal.Snapshot()[0].Event.EventId);
    }

    [TestMethod]
    public async Task AnchoredCrashInterval_RecoversWithoutDuplicateEvent()
    {
        var inner = new MemoryExecutionProofStore();
        var anchor = new FailFirstAdvanceAnchor();
        var anchored = new AnchoredExecutionProofStore(inner, anchor);
        var journal = await DurableExecutionProofJournal.OpenAsync(Project, anchored);

        await Assert.ThrowsExactlyAsync<IOException>(() => journal.AppendAndPersistAsync(Started()));

        Assert.AreEqual(ExecutionProofHead.Empty, journal.Head);
        Assert.IsTrue(journal.HasPendingRecovery);
        var innerAfterFailure = await inner.LoadAsync(Project);
        Assert.IsNotNull(innerAfterFailure);
        Assert.AreEqual(1L, innerAfterFailure.Head.EntryCount);
        Assert.IsNull(await anchor.LoadAsync(Project));

        var recovered = await journal.RecoverPendingAsync();

        Assert.AreEqual(1L, recovered.EntryCount);
        Assert.AreEqual(recovered, await anchor.LoadAsync(Project));
        Assert.AreEqual(1, journal.Snapshot().Count);
        Assert.AreEqual("event-start", journal.Snapshot()[0].Event.EventId);
        ExecutionProofLedger.VerifySnapshot(journal.Snapshot(), journal.Head);
    }

    [TestMethod]
    public async Task PromotionEvidence_IsUnavailableUntilAuthorizationPersistenceRecovers()
    {
        var inner = new MemoryExecutionProofStore();
        var store = new FailOnSaveNumberStore(inner, failOnSaveNumber: 5);
        var journal = await DurableExecutionProofJournal.OpenAsync(Project, store);

        await journal.AppendAndPersistAsync(Started());
        await journal.AppendAndPersistAsync(Completed());
        await journal.AppendAndPersistAsync(Validation());
        await journal.AppendAndPersistAsync(Judge());
        await Assert.ThrowsExactlyAsync<IOException>(() => journal.AppendAndPersistAsync(Authorization()));

        Assert.AreEqual(4L, journal.Head.EntryCount);
        Assert.IsTrue(journal.HasPendingRecovery);
        Assert.ThrowsExactly<InvalidOperationException>(() => journal.BuildPromotionEvidence("exec-001"));

        await journal.RecoverPendingAsync();
        var evidence = journal.BuildPromotionEvidence("exec-001");

        Assert.AreEqual(5L, evidence.LedgerHead.EntryCount);
        Assert.AreEqual(journal.Head, evidence.LedgerHead);
        Assert.AreEqual(ArtifactHash, evidence.ArtifactManifestSha256);
    }

    [TestMethod]
    public async Task Refresh_AcceptsStrictCanonicalExtension()
    {
        var store = new MemoryExecutionProofStore();
        var first = new ExecutionProofLedger();
        first.Append(Started());
        await store.SaveAsync(Project, first.Snapshot(), first.Head);
        var journal = await DurableExecutionProofJournal.OpenAsync(Project, store);

        var extended = Replay(first);
        extended.Append(Completed());
        await store.SaveAsync(Project, extended.Snapshot(), extended.Head);

        var refreshed = await journal.RefreshAsync();

        Assert.AreEqual(2L, refreshed.EntryCount);
        Assert.AreEqual(extended.Head, journal.Head);
        CollectionAssert.AreEqual(extended.Snapshot().ToArray(), journal.Snapshot().ToArray());
    }

    [TestMethod]
    public async Task Refresh_RejectsRollbackAndSameHeightFork()
    {
        var store = new MemoryExecutionProofStore();
        var initial = new ExecutionProofLedger();
        initial.Append(Started());
        initial.Append(Completed());
        await store.SaveAsync(Project, initial.Snapshot(), initial.Head);
        var journal = await DurableExecutionProofJournal.OpenAsync(Project, store);

        var rollback = new ExecutionProofLedger();
        rollback.Append(Started());
        store.Replace(Project, rollback.Snapshot(), rollback.Head);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => journal.RefreshAsync());
        Assert.AreEqual(initial.Head, journal.Head);

        var fork = new ExecutionProofLedger();
        fork.Append(Started() with { EventId = "event-fork-start" });
        fork.Append(Completed() with { EventId = "event-fork-complete" });
        store.Replace(Project, fork.Snapshot(), fork.Head);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => journal.RefreshAsync());
        Assert.AreEqual(initial.Head, journal.Head);
    }

    [TestMethod]
    public async Task CrossProjectEvent_IsRejectedBeforePersistence()
    {
        var store = new MemoryExecutionProofStore();
        var journal = await DurableExecutionProofJournal.OpenAsync(Project, store);
        var otherProject = Started() with { ProjectId = Guid.Parse("61616161-6161-6161-6161-616161616161") };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => journal.AppendAndPersistAsync(otherProject));

        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual(ExecutionProofHead.Empty, journal.Head);
    }

    private static ExecutionProofLedger Replay(ExecutionProofLedger source)
    {
        var copy = new ExecutionProofLedger();
        foreach (var record in source.Snapshot())
        {
            var replayed = copy.Append(record.Event);
            Assert.AreEqual(record, replayed);
        }
        return copy;
    }

    private static ExecutionProofEvent Started() => new(
        "event-start", Project, "run-001", "exec-001", ExecutionProofStage.Started,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Pending,
        InputHash, AuthorityHash, null, null, null, null, null, null, null, At(0));

    private static ExecutionProofEvent Completed() => new(
        "event-complete", Project, "run-001", "exec-001", ExecutionProofStage.Completed,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Succeeded,
        InputHash, AuthorityHash, ResultHash, AttestationHash, ArtifactHash, null, null, null, null, At(1));

    private static ExecutionProofEvent Validation() => new(
        "event-validation", Project, "run-001", "exec-001", ExecutionProofStage.ValidationCompleted,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Succeeded,
        InputHash, AuthorityHash, ResultHash, AttestationHash, ArtifactHash, ValidationHash, null, null, null, At(2));

    private static ExecutionProofEvent Judge() => new(
        "event-judge", Project, "run-001", "exec-001", ExecutionProofStage.JudgeDecided,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Approved,
        InputHash, AuthorityHash, ResultHash, AttestationHash, ArtifactHash, ValidationHash, JudgeHash, null, null, At(3));

    private static ExecutionProofEvent Authorization() => new(
        "event-authorize", Project, "run-001", "exec-001", ExecutionProofStage.PromotionAuthorized,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Approved,
        InputHash, AuthorityHash, ResultHash, AttestationHash, ArtifactHash, ValidationHash, JudgeHash, PromotionHash, null, At(4));

    private static DateTimeOffset At(int minutes) =>
        new DateTimeOffset(2026, 8, 15, 18, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static string Hash(char value) => new(value, 64);
    private static string InputHash => Hash('a');
    private static string AuthorityHash => Hash('b');
    private static string ResultHash => Hash('c');
    private static string AttestationHash => Hash('d');
    private static string ArtifactHash => Hash('e');
    private static string ValidationHash => Hash('f');
    private static string JudgeHash => Hash('1');
    private static string PromotionHash => Hash('2');

    private sealed class MemoryExecutionProofStore : IExecutionProofStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, StoredExecutionProofSnapshot> _snapshots = [];

        public int SaveCount { get; private set; }

        public Task<StoredExecutionProofSnapshot?> LoadAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_snapshots.TryGetValue(projectId, out var snapshot))
                    return Task.FromResult<StoredExecutionProofSnapshot?>(null);
                return Task.FromResult<StoredExecutionProofSnapshot?>(Clone(snapshot));
            }
        }

        public Task SaveAsync(
            Guid projectId,
            IReadOnlyList<ExecutionProofRecord> records,
            ExecutionProofHead head,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionProofLedger.VerifySnapshot(records, head);
            lock (_sync)
            {
                SaveCount++;
                _snapshots[projectId] = new StoredExecutionProofSnapshot(projectId, records.ToArray(), head);
            }
            return Task.CompletedTask;
        }

        public void Replace(
            Guid projectId,
            IReadOnlyList<ExecutionProofRecord> records,
            ExecutionProofHead head)
        {
            ExecutionProofLedger.VerifySnapshot(records, head);
            lock (_sync)
            {
                _snapshots[projectId] = new StoredExecutionProofSnapshot(projectId, records.ToArray(), head);
            }
        }

        private static StoredExecutionProofSnapshot Clone(StoredExecutionProofSnapshot snapshot) =>
            new(snapshot.ProjectId, snapshot.Records.ToArray(), snapshot.Head);
    }

    private sealed class FailBeforePersistOnceStore(IExecutionProofStore inner) : IExecutionProofStore
    {
        private int _failed;

        public Task<StoredExecutionProofSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(projectId, cancellationToken);

        public Task SaveAsync(
            Guid projectId,
            IReadOnlyList<ExecutionProofRecord> records,
            ExecutionProofHead head,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
                throw new IOException("synthetic pre-persist failure");
            return inner.SaveAsync(projectId, records, head, cancellationToken);
        }
    }

    private sealed class FailOnSaveNumberStore(IExecutionProofStore inner, int failOnSaveNumber) : IExecutionProofStore
    {
        private int _saveCount;

        public Task<StoredExecutionProofSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(projectId, cancellationToken);

        public Task SaveAsync(
            Guid projectId,
            IReadOnlyList<ExecutionProofRecord> records,
            ExecutionProofHead head,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _saveCount);
            if (attempt == failOnSaveNumber)
                throw new IOException("synthetic selected save failure");
            return inner.SaveAsync(projectId, records, head, cancellationToken);
        }
    }

    private sealed class FailFirstAdvanceAnchor : IExecutionProofHeadAnchor
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, ExecutionProofHead> _heads = [];
        private int _failed;

        public Task<ExecutionProofHead?> LoadAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                return Task.FromResult(_heads.TryGetValue(projectId, out var head) ? head : null);
            }
        }

        public Task AdvanceAsync(
            Guid projectId,
            ExecutionProofHead expectedPrevious,
            ExecutionProofHead next,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _failed, 1) == 0)
                throw new IOException("synthetic anchor failure after snapshot write");

            lock (_sync)
            {
                var current = _heads.TryGetValue(projectId, out var head)
                    ? head
                    : ExecutionProofHead.Empty;
                if (current != expectedPrevious)
                    throw new InvalidOperationException("synthetic anchor CAS conflict");
                _heads[projectId] = next;
            }
            return Task.CompletedTask;
        }
    }
}
