using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AnchoredExecutionProofReconciliationTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task ReconcileAsync_SnapshotExactlyOneHeadAhead_CompletesPendingCas()
    {
        var inner = new MemoryStore();
        var anchor = new MemoryAnchor();
        var store = new AnchoredExecutionProofStore(inner, anchor);
        var ledger = new ExecutionProofLedger();
        ledger.Append(Event("start", ExecutionProofStage.Started, ExecutionProofOutcome.Pending));
        await store.SaveAsync(Project, ledger.Snapshot(), ledger.Head);

        var predecessor = ledger.Head;
        ledger.Append(Event("complete", ExecutionProofStage.Completed, ExecutionProofOutcome.Succeeded,
            result: H('c'), attestation: H('d'), artifact: H('e')));
        await inner.SaveAsync(Project, ledger.Snapshot(), ledger.Head); // crash before anchor CAS

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(Project));
        Assert.AreEqual(predecessor, await anchor.LoadAsync(Project));

        var reconciled = await store.ReconcileAsync(Project);
        Assert.IsNotNull(reconciled);
        Assert.AreEqual(ledger.Head, reconciled.Head);
        Assert.AreEqual(ledger.Head, await anchor.LoadAsync(Project));
        Assert.AreEqual(2, anchor.AdvanceCount);
    }

    [TestMethod]
    public async Task ReconcileAsync_AlreadyAligned_IsIdempotent()
    {
        var inner = new MemoryStore();
        var anchor = new MemoryAnchor();
        var store = new AnchoredExecutionProofStore(inner, anchor);
        var ledger = new ExecutionProofLedger();
        ledger.Append(Event("start", ExecutionProofStage.Started, ExecutionProofOutcome.Pending));
        await store.SaveAsync(Project, ledger.Snapshot(), ledger.Head);

        var before = anchor.AdvanceCount;
        var reconciled = await store.ReconcileAsync(Project);

        Assert.IsNotNull(reconciled);
        Assert.AreEqual(ledger.Head, reconciled.Head);
        Assert.AreEqual(before, anchor.AdvanceCount);
    }

    [TestMethod]
    public async Task ReconcileAsync_FirstSnapshotWithNoAnchor_AdvancesFromGenesis()
    {
        var inner = new MemoryStore();
        var anchor = new MemoryAnchor();
        var ledger = new ExecutionProofLedger();
        ledger.Append(Event("start", ExecutionProofStage.Started, ExecutionProofOutcome.Pending));
        await inner.SaveAsync(Project, ledger.Snapshot(), ledger.Head);
        var store = new AnchoredExecutionProofStore(inner, anchor);

        var reconciled = await store.ReconcileAsync(Project);

        Assert.IsNotNull(reconciled);
        Assert.AreEqual(ledger.Head, await anchor.LoadAsync(Project));
        Assert.AreEqual(1, anchor.AdvanceCount);
    }

    [TestMethod]
    public async Task ReconcileAsync_AnchorIsNeitherHeadNorPredecessor_RejectsFork()
    {
        var inner = new MemoryStore();
        var anchor = new MemoryAnchor();
        var ledger = new ExecutionProofLedger();
        ledger.Append(Event("start", ExecutionProofStage.Started, ExecutionProofOutcome.Pending));
        ledger.Append(Event("complete", ExecutionProofStage.Completed, ExecutionProofOutcome.Succeeded,
            result: H('c'), attestation: H('d'), artifact: H('e')));
        await inner.SaveAsync(Project, ledger.Snapshot(), ledger.Head);
        await anchor.ForceAsync(Project, new ExecutionProofHead(1, H('9')));
        var store = new AnchoredExecutionProofStore(inner, anchor);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReconcileAsync(Project));
        Assert.AreEqual(H('9'), (await anchor.LoadAsync(Project))!.HeadHashSha256);
    }

    private static ExecutionProofEvent Event(
        string id,
        ExecutionProofStage stage,
        ExecutionProofOutcome outcome,
        string? result = null,
        string? attestation = null,
        string? artifact = null) => new(
            "event-reconcile-" + id,
            Project,
            "run-reconcile",
            "exec-reconcile",
            stage,
            "coding-agent",
            "sandbox-worker",
            outcome,
            H('a'), H('b'), result, attestation, artifact, null, null, null, null,
            new DateTimeOffset(2026, 8, 15, 21, 0, 0, TimeSpan.Zero).AddMinutes((int)stage));

    private static string H(char value) => new(value, 64);

    private sealed class MemoryStore : IExecutionProofStore
    {
        private StoredExecutionProofSnapshot? _snapshot;

        public Task<StoredExecutionProofSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshot);
        }

        public Task SaveAsync(Guid projectId, IReadOnlyList<ExecutionProofRecord> records, ExecutionProofHead head,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _snapshot = new StoredExecutionProofSnapshot(projectId, records.ToArray(), head);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryAnchor : IExecutionProofHeadAnchor
    {
        private ExecutionProofHead? _head;
        public int AdvanceCount { get; private set; }

        public Task<ExecutionProofHead?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_head);
        }

        public Task AdvanceAsync(Guid projectId, ExecutionProofHead expectedPrevious, ExecutionProofHead next,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = _head ?? ExecutionProofHead.Empty;
            if (current != expectedPrevious) throw new InvalidOperationException("synthetic CAS mismatch");
            if (next.EntryCount != expectedPrevious.EntryCount + 1) throw new InvalidOperationException("non-monotonic advance");
            _head = next;
            AdvanceCount++;
            return Task.CompletedTask;
        }

        public Task ForceAsync(Guid projectId, ExecutionProofHead head)
        {
            _head = head;
            return Task.CompletedTask;
        }
    }
}
