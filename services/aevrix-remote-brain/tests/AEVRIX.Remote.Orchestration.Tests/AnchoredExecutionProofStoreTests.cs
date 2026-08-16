using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AnchoredExecutionProofStoreTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task SaveAndLoad_AdvanceMonotonicAnchorOneHeadAtATime()
    {
        using var temp = new TempDirectory();
        var inner = new EncryptedExecutionProofStore(temp.Path, new ProjectKeyProvider());
        var anchor = new MemoryAnchor();
        var store = new AnchoredExecutionProofStore(inner, anchor);
        var persistent = await PersistentExecutionProofLedger.OpenAsync(Project, store);

        foreach (var item in EventsThroughAuthorization()) await persistent.AppendAsync(item);

        var loaded = await store.LoadAsync(Project);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(5, loaded.Head.EntryCount);
        Assert.AreEqual(loaded.Head, await anchor.LoadAsync(Project));
        ExecutionProofLedger.VerifySnapshot(loaded.Records, loaded.Head);
    }

    [TestMethod]
    public async Task RestoringOlderValidCiphertext_IsDetectedByNewerExternalAnchor()
    {
        using var temp = new TempDirectory();
        var inner = new EncryptedExecutionProofStore(temp.Path, new ProjectKeyProvider());
        var anchor = new MemoryAnchor();
        var store = new AnchoredExecutionProofStore(inner, anchor);
        var persistent = await PersistentExecutionProofLedger.OpenAsync(Project, store);

        await persistent.AppendAsync(Started());
        var path = Directory.EnumerateFiles(temp.Path, "*.aevx").Single();
        var oldCiphertext = File.ReadAllBytes(path);

        foreach (var item in EventsAfterStartThroughAuthorization()) await persistent.AppendAsync(item);
        var newestHead = persistent.Head;
        File.WriteAllBytes(path, oldCiphertext);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(Project));
        Assert.AreEqual(newestHead, await anchor.LoadAsync(Project));
    }

    [TestMethod]
    public async Task SnapshotAheadOfAnchor_FailsClosedAndSameSaveCanCompleteCas()
    {
        using var temp = new TempDirectory();
        var inner = new EncryptedExecutionProofStore(temp.Path, new ProjectKeyProvider());
        var anchor = new MemoryAnchor();
        var store = new AnchoredExecutionProofStore(inner, anchor);
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started());
        await store.SaveAsync(Project, ledger.Snapshot(), ledger.Head);

        ledger.Append(Completed());
        await inner.SaveAsync(Project, ledger.Snapshot(), ledger.Head); // simulate crash before external CAS

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(Project));

        await store.SaveAsync(Project, ledger.Snapshot(), ledger.Head); // retry completes predecessor CAS
        var loaded = await store.LoadAsync(Project);
        Assert.IsNotNull(loaded);
        Assert.AreEqual(2, loaded.Head.EntryCount);
    }

    [TestMethod]
    public async Task StaleOrForgedAnchorAdvance_IsRejectedByCasAuthority()
    {
        var anchor = new MemoryAnchor();
        var first = new ExecutionProofHead(1, H('1'));
        var second = new ExecutionProofHead(2, H('2'));
        await anchor.AdvanceAsync(Project, ExecutionProofHead.Empty, first);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            anchor.AdvanceAsync(Project, ExecutionProofHead.Empty, second));
        Assert.AreEqual(first, await anchor.LoadAsync(Project));
    }

    [TestMethod]
    public async Task ReSavingAlreadyAnchoredExactHead_IsIdempotent()
    {
        using var temp = new TempDirectory();
        var inner = new EncryptedExecutionProofStore(temp.Path, new ProjectKeyProvider());
        var anchor = new MemoryAnchor();
        var store = new AnchoredExecutionProofStore(inner, anchor);
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started());

        await store.SaveAsync(Project, ledger.Snapshot(), ledger.Head);
        var anchored = await anchor.LoadAsync(Project);
        await store.SaveAsync(Project, ledger.Snapshot(), ledger.Head);

        Assert.AreEqual(anchored, await anchor.LoadAsync(Project));
        Assert.AreEqual(1, anchor.AdvanceCount);
    }

    [TestMethod]
    public async Task AnchorWithoutSnapshot_AndSnapshotWithoutAnchor_BothFailClosed()
    {
        using var firstTemp = new TempDirectory();
        var anchorOnly = new MemoryAnchor();
        await anchorOnly.AdvanceAsync(Project, ExecutionProofHead.Empty, new ExecutionProofHead(1, H('1')));
        var first = new AnchoredExecutionProofStore(
            new EncryptedExecutionProofStore(firstTemp.Path, new ProjectKeyProvider()), anchorOnly);
        await Assert.ThrowsAsync<InvalidDataException>(() => first.LoadAsync(Project));

        using var secondTemp = new TempDirectory();
        var innerOnly = new EncryptedExecutionProofStore(secondTemp.Path, new ProjectKeyProvider());
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started());
        await innerOnly.SaveAsync(Project, ledger.Snapshot(), ledger.Head);
        var second = new AnchoredExecutionProofStore(innerOnly, new MemoryAnchor());
        await Assert.ThrowsAsync<InvalidDataException>(() => second.LoadAsync(Project));
    }

    private static IReadOnlyList<ExecutionProofEvent> EventsThroughAuthorization() =>
        [Started(), .. EventsAfterStartThroughAuthorization()];

    private static IReadOnlyList<ExecutionProofEvent> EventsAfterStartThroughAuthorization() =>
    [
        Completed(),
        Event("event-validation", ExecutionProofStage.ValidationCompleted, ExecutionProofOutcome.Succeeded,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), minute: 2),
        Event("event-judge", ExecutionProofStage.JudgeDecided, ExecutionProofOutcome.Approved,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), judge: H('1'), minute: 3),
        Event("event-authorize", ExecutionProofStage.PromotionAuthorized, ExecutionProofOutcome.Approved,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), judge: H('1'), promotion: H('2'), minute: 4)
    ];

    private static ExecutionProofEvent Started() =>
        new("event-start", Project, "run-anchor", "exec-anchor", ExecutionProofStage.Started,
            "coding-agent", "sandbox-worker", ExecutionProofOutcome.Pending,
            H('a'), H('b'), null, null, null, null, null, null, null, At(0));

    private static ExecutionProofEvent Completed() =>
        Event("event-complete", ExecutionProofStage.Completed, ExecutionProofOutcome.Succeeded,
            result: H('c'), attestation: H('d'), artifact: H('e'), minute: 1);

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
        int minute = 0) =>
        new(id, Project, "run-anchor", "exec-anchor", stage, "coding-agent", "sandbox-worker", outcome,
            H('a'), H('b'), result, attestation, artifact, validation, judge, promotion, null, At(minute));

    private static DateTimeOffset At(int minute) =>
        new DateTimeOffset(2026, 8, 15, 17, 0, 0, TimeSpan.Zero).AddMinutes(minute);

    private static string H(char value) => new(value, 64);

    private sealed class MemoryAnchor : IExecutionProofHeadAnchor
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, ExecutionProofHead> _heads = [];
        public int AdvanceCount { get; private set; }

        public Task<ExecutionProofHead?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
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
            lock (_sync)
            {
                var current = _heads.TryGetValue(projectId, out var head) ? head : ExecutionProofHead.Empty;
                if (current != expectedPrevious)
                    throw new InvalidOperationException("Anchor CAS predecessor mismatch.");
                if (next.EntryCount != expectedPrevious.EntryCount + 1)
                    throw new InvalidOperationException("Anchor may advance exactly one execution-proof record at a time.");
                _heads[projectId] = next;
                AdvanceCount++;
                return Task.CompletedTask;
            }
        }
    }

    private sealed class ProjectKeyProvider : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(
                System.Security.Cryptography.SHA256.HashData(projectId.ToByteArray()));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-anchor-tests", Guid.NewGuid().ToString("N"));
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
