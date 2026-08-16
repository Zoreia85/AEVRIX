using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class ProofRecordingMissionSpecialistTests
{
    private static readonly Guid Project = Guid.Parse("81818181-8181-8181-8181-818181818181");
    private const string Objective = "Sensitive objective that must never enter the proof ledger.";
    private const string Summary = "Sensitive specialist summary that must remain outside the proof ledger.";

    [TestMethod]
    public async Task ExecuteAsync_PersistsStartedAndSucceededCompletionBeforeReturningOutput()
    {
        var store = new MemoryProofStore();
        var registry = new ExecutionProofJournalRegistry(store);
        var inner = SuccessSpecialist();
        var wrapper = Wrapper(inner, registry);

        var output = await wrapper.ExecuteAsync(Context());
        var journal = await registry.GetAsync(Project);
        var records = journal.Snapshot();

        Assert.AreEqual(Summary, output.Summary);
        Assert.AreEqual(1, inner.CallCount);
        Assert.AreEqual(2, records.Count);
        Assert.AreEqual(ExecutionProofStage.Started, records[0].Event.Stage);
        Assert.AreEqual(ExecutionProofOutcome.Pending, records[0].Event.Outcome);
        Assert.AreEqual(ExecutionProofStage.Completed, records[1].Event.Stage);
        Assert.AreEqual(ExecutionProofOutcome.Succeeded, records[1].Event.Outcome);
        Assert.AreEqual(records[0].Event.ExecutionId, records[1].Event.ExecutionId);
        Assert.AreEqual(
            MissionExecutionProofIdentity.CreateExecutionId(
                Project,
                "mission-proof-001",
                "target-001",
                "inspect-001",
                MissionSpecialistKind.StaticAnalysis),
            records[0].Event.ExecutionId);
        Assert.AreEqual(records[0].Event.InputDigestSha256, records[1].Event.InputDigestSha256);
        Assert.AreEqual(records[0].Event.AuthorityDigestSha256, records[1].Event.AuthorityDigestSha256);
        Assert.IsNotNull(records[1].Event.ResultDigestSha256);
        Assert.IsNotNull(records[1].Event.ArtifactManifestSha256);
        AssertLedgerContainsNoRawPayload(records, Objective, Summary);
        ExecutionProofLedger.VerifySnapshot(records, journal.Head);
    }

    [TestMethod]
    public async Task ExecuteAsync_RecoversItsOwnFailedStartedPersistenceBeforeInvokingSpecialist()
    {
        var durable = new MemoryProofStore();
        var store = new FailSelectedSavesStore(durable, 1);
        var registry = new ExecutionProofJournalRegistry(store);
        var inner = SuccessSpecialist();
        var wrapper = Wrapper(inner, registry);

        var output = await wrapper.ExecuteAsync(Context());
        var journal = await registry.GetAsync(Project);

        Assert.AreEqual(Summary, output.Summary);
        Assert.AreEqual(1, inner.CallCount);
        Assert.IsFalse(journal.HasPendingRecovery);
        Assert.AreEqual(2L, journal.Head.EntryCount);
        Assert.AreEqual(ExecutionProofOutcome.Succeeded, journal.Snapshot()[1].Event.Outcome);
    }

    [TestMethod]
    public async Task ExecuteAsync_BlocksSpecialistWhenStartedRecoveryCannotComplete()
    {
        var durable = new MemoryProofStore();
        var store = new FailSelectedSavesStore(durable, 1, 2);
        var registry = new ExecutionProofJournalRegistry(store);
        var inner = SuccessSpecialist();
        var wrapper = Wrapper(inner, registry);

        await Assert.ThrowsExactlyAsync<IOException>(() => wrapper.ExecuteAsync(Context()));
        var journal = await registry.GetAsync(Project);

        Assert.AreEqual(0, inner.CallCount);
        Assert.IsTrue(journal.HasPendingRecovery);
        Assert.AreEqual(ExecutionProofHead.Empty, journal.Head);
    }

    [TestMethod]
    public async Task ExecuteAsync_DoesNotReturnSuccessWhenCompletedPersistenceFails()
    {
        var durable = new MemoryProofStore();
        var store = new FailSelectedSavesStore(durable, 2);
        var registry = new ExecutionProofJournalRegistry(store);
        var inner = SuccessSpecialist();
        var wrapper = Wrapper(inner, registry);

        await Assert.ThrowsExactlyAsync<IOException>(() => wrapper.ExecuteAsync(Context()));
        var journal = await registry.GetAsync(Project);

        Assert.AreEqual(1, inner.CallCount);
        Assert.IsTrue(journal.HasPendingRecovery);
        Assert.AreEqual(1L, journal.Head.EntryCount);
        Assert.AreEqual(ExecutionProofStage.Started, journal.Snapshot().Single().Event.Stage);

        await journal.RecoverPendingAsync();
        Assert.AreEqual(2L, journal.Head.EntryCount);
        Assert.IsFalse(journal.HasPendingRecovery);
        Assert.AreEqual(ExecutionProofOutcome.Succeeded, journal.Snapshot()[1].Event.Outcome);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => wrapper.ExecuteAsync(Context()));
        Assert.AreEqual(1, inner.CallCount, "Recovered terminal proof must never cause specialist replay.");
    }

    [TestMethod]
    public async Task ExecuteAsync_RecordsFailedCompletionWithoutExceptionMessage()
    {
        const string secretMessage = "secret failure detail must never be persisted";
        var store = new MemoryProofStore();
        var registry = new ExecutionProofJournalRegistry(store);
        var inner = new DelegateSpecialist(
            MissionSpecialistKind.StaticAnalysis,
            (_, _) => throw new IOException(secretMessage));
        var wrapper = Wrapper(inner, registry);

        var exception = await Assert.ThrowsExactlyAsync<IOException>(() => wrapper.ExecuteAsync(Context()));
        var journal = await registry.GetAsync(Project);
        var records = journal.Snapshot();

        Assert.AreEqual(secretMessage, exception.Message);
        Assert.AreEqual(1, inner.CallCount);
        Assert.AreEqual(2, records.Count);
        Assert.AreEqual(ExecutionProofOutcome.Failed, records[1].Event.Outcome);
        Assert.IsNotNull(records[1].Event.ResultDigestSha256);
        Assert.IsNull(records[1].Event.ArtifactManifestSha256);
        AssertLedgerContainsNoRawPayload(records, secretMessage, Objective);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsEvidenceEscalationAndRecordsFailure()
    {
        var store = new MemoryProofStore();
        var registry = new ExecutionProofJournalRegistry(store);
        var inner = new DelegateSpecialist(
            MissionSpecialistKind.StaticAnalysis,
            (_, _) => Task.FromResult(new SpecialistExecutionOutput(
                Summary,
                0.91,
                ["ev-001", "ev-forged"],
                ["artifact-001"])));
        var wrapper = Wrapper(inner, registry);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => wrapper.ExecuteAsync(Context()));
        var journal = await registry.GetAsync(Project);

        Assert.AreEqual(1, inner.CallCount);
        Assert.AreEqual(ExecutionProofOutcome.Failed, journal.Snapshot()[1].Event.Outcome);
        Assert.IsNull(journal.Snapshot()[1].Event.ArtifactManifestSha256);
    }

    [TestMethod]
    public async Task ExecuteAsync_CancellationAfterStartedClaimClosesProofAsFailed()
    {
        var store = new MemoryProofStore();
        var registry = new ExecutionProofJournalRegistry(store);
        using var cancellation = new CancellationTokenSource();
        var inner = new DelegateSpecialist(
            MissionSpecialistKind.StaticAnalysis,
            async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("unreachable");
            });
        var wrapper = Wrapper(inner, registry);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            wrapper.ExecuteAsync(Context(), cancellation.Token));
        var journal = await registry.GetAsync(Project);

        Assert.AreEqual(1, inner.CallCount);
        Assert.AreEqual(2L, journal.Head.EntryCount);
        Assert.IsFalse(journal.HasPendingRecovery);
        Assert.AreEqual(ExecutionProofOutcome.Failed, journal.Snapshot()[1].Event.Outcome);
    }

    [TestMethod]
    public async Task ExecuteAsync_PreexistingStartedOnlyExecutionIsNeverBlindlyReplayed()
    {
        var store = new MemoryProofStore();
        var registry = new ExecutionProofJournalRegistry(store);
        var firstInner = SuccessSpecialist();
        var firstWrapper = Wrapper(firstInner, registry);
        var failingStore = new FailSelectedSavesStore(store, 2);
        var failingRegistry = new ExecutionProofJournalRegistry(failingStore);
        var failingInner = SuccessSpecialist();
        var failingWrapper = Wrapper(failingInner, failingRegistry);

        // Create a separately persisted Started-only state: terminal persistence fails and then the
        // candidate is deliberately not recovered, simulating an execution boundary interruption.
        await Assert.ThrowsExactlyAsync<IOException>(() => failingWrapper.ExecuteAsync(Context()));
        var pendingJournal = await failingRegistry.GetAsync(Project);
        Assert.AreEqual(1L, pendingJournal.Head.EntryCount);
        Assert.IsTrue(pendingJournal.HasPendingRecovery);

        // A fresh process view cannot load the mismatched pending store safely in anchored
        // production. For this in-memory test, discard the failed terminal write and preserve only
        // the canonical Started snapshot to model a clean restart after Started was committed.
        var started = pendingJournal.Snapshot();
        store.Replace(Project, started, pendingJournal.Head);
        var freshRegistry = new ExecutionProofJournalRegistry(store);
        var freshInner = SuccessSpecialist();
        var freshWrapper = Wrapper(freshInner, freshRegistry);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => freshWrapper.ExecuteAsync(Context()));
        Assert.AreEqual(0, freshInner.CallCount);
        Assert.AreEqual(0, firstInner.CallCount);
        _ = firstWrapper; // Explicitly document that no alternate wrapper is used to resume work.
    }

    [TestMethod]
    public async Task ExecuteAsync_ConcurrentDuplicateClaimAllowsOnlyOneSpecialistInvocation()
    {
        var store = new MemoryProofStore();
        var registry = new ExecutionProofJournalRegistry(store);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new DelegateSpecialist(
            MissionSpecialistKind.StaticAnalysis,
            async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new SpecialistExecutionOutput(Summary, 0.95, ["ev-001"], ["artifact-001"]);
            });
        var wrapper = Wrapper(inner, registry);

        var first = wrapper.ExecuteAsync(Context());
        await entered.Task;
        var second = wrapper.ExecuteAsync(Context());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second);
        release.TrySetResult();
        await first;

        Assert.AreEqual(1, inner.CallCount);
        var journal = await registry.GetAsync(Project);
        Assert.AreEqual(2L, journal.Head.EntryCount);
    }

    [TestMethod]
    public async Task Registry_ReturnsOneJournalPerProjectAndSeparatesProjects()
    {
        var store = new MemoryProofStore();
        var registry = new ExecutionProofJournalRegistry(store);
        var otherProject = Guid.Parse("82828282-8282-8282-8282-828282828282");

        var first = await registry.GetAsync(Project);
        var second = await registry.GetAsync(Project);
        var other = await registry.GetAsync(otherProject);

        Assert.AreSame(first, second);
        Assert.AreNotSame(first, other);
        Assert.AreEqual(Project, first.ProjectId);
        Assert.AreEqual(otherProject, other.ProjectId);
    }

    [TestMethod]
    public async Task MissionDirector_UsesDecoratorWithoutInternalDirectorChanges()
    {
        var store = new MemoryProofStore();
        var registry = new ExecutionProofJournalRegistry(store);
        var director = MissionDirector.CreateProofBound(
            [SuccessSpecialist()],
            registry,
            new FixedTimeProvider(),
            new ProofRecordingMissionSpecialistOptions(TimeSpan.FromSeconds(2)));
        var task = TaskSpec();
        var plan = new MissionPlan("mission-proof-001", Project, "target-001", [task]);

        var result = await director.ExecuteAsync(plan);
        var journal = await registry.GetAsync(Project);

        Assert.IsTrue(result.RequiredTasksSucceeded);
        Assert.AreEqual(MissionTaskState.Succeeded, result.TaskResults.Single().State);
        Assert.AreEqual(2L, journal.Head.EntryCount);
        Assert.AreEqual(ExecutionProofOutcome.Succeeded, journal.Snapshot()[1].Event.Outcome);
    }

    private static ProofRecordingMissionSpecialist Wrapper(
        IMissionSpecialist inner,
        IExecutionProofJournalProvider registry) =>
        new(
            inner,
            registry,
            new FixedTimeProvider(),
            new ProofRecordingMissionSpecialistOptions(TimeSpan.FromSeconds(2)));

    private static DelegateSpecialist SuccessSpecialist() => new(
        MissionSpecialistKind.StaticAnalysis,
        (_, _) => Task.FromResult(new SpecialistExecutionOutput(
            Summary,
            0.95,
            ["ev-001"],
            ["artifact-001"])));

    private static SpecialistExecutionContext Context() => new(
        "mission-proof-001",
        Project,
        "target-001",
        TaskSpec(),
        new Dictionary<string, SpecialistTaskResult>(StringComparer.OrdinalIgnoreCase));

    private static MissionTaskSpec TaskSpec() => new(
        "inspect-001",
        MissionSpecialistKind.StaticAnalysis,
        Objective,
        ["ev-001"],
        [],
        Required: true);

    private static void AssertLedgerContainsNoRawPayload(
        IReadOnlyList<ExecutionProofRecord> records,
        params string[] forbidden)
    {
        foreach (var record in records)
        {
            var serializedMetadata = record.Event.ToString();
            foreach (var value in forbidden)
            {
                Assert.IsFalse(
                    serializedMetadata.Contains(value, StringComparison.Ordinal),
                    "Execution ledger exposed raw payload material.");
            }
        }
    }

    private sealed class DelegateSpecialist : IMissionSpecialist
    {
        private readonly Func<SpecialistExecutionContext, CancellationToken, Task<SpecialistExecutionOutput>> _execute;
        private int _callCount;

        public DelegateSpecialist(
            MissionSpecialistKind kind,
            Func<SpecialistExecutionContext, CancellationToken, Task<SpecialistExecutionOutput>> execute)
        {
            Kind = kind;
            _execute = execute;
        }

        public MissionSpecialistKind Kind { get; }
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return _execute(context, cancellationToken);
        }
    }

    private sealed class MemoryProofStore : IExecutionProofStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, StoredExecutionProofSnapshot> _snapshots = [];

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

    private sealed class FailSelectedSavesStore : IExecutionProofStore
    {
        private readonly IExecutionProofStore _inner;
        private readonly HashSet<int> _failAttempts;
        private int _attempt;

        public FailSelectedSavesStore(IExecutionProofStore inner, params int[] failAttempts)
        {
            _inner = inner;
            _failAttempts = failAttempts.ToHashSet();
        }

        public Task<StoredExecutionProofSnapshot?> LoadAsync(
            Guid projectId,
            CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(projectId, cancellationToken);

        public Task SaveAsync(
            Guid projectId,
            IReadOnlyList<ExecutionProofRecord> records,
            ExecutionProofHead head,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _attempt);
            if (_failAttempts.Contains(attempt))
                throw new IOException($"synthetic save failure {attempt}");
            return _inner.SaveAsync(projectId, records, head, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 15, 22, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
