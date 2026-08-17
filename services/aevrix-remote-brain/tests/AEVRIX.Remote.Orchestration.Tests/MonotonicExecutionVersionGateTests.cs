using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class MonotonicExecutionVersionGateTests
{
    [TestMethod]
    public async Task EnsureAllowedAsync_RejectsMissingExternalFloor()
    {
        var anchor = new InMemoryFloorAnchor();
        var gate = new MonotonicExecutionVersionGate(anchor, "runtime:default");
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.EnsureAllowedAsync(new ExecutionVersionStamp(5, 7)));
    }

    [TestMethod]
    public async Task EnsureAllowedAsync_RejectsBackendDowngrade()
    {
        var anchor = new InMemoryFloorAnchor(new ExecutionVersionFloor(5, 7));
        var gate = new MonotonicExecutionVersionGate(anchor, "runtime:default");
        await Assert.ThrowsAsync<InvalidDataException>(() => gate.EnsureAllowedAsync(new ExecutionVersionStamp(4, 7)));
    }

    [TestMethod]
    public async Task EnsureAllowedAsync_RejectsAuthorityPolicyDowngrade()
    {
        var anchor = new InMemoryFloorAnchor(new ExecutionVersionFloor(5, 7));
        var gate = new MonotonicExecutionVersionGate(anchor, "runtime:default");
        await Assert.ThrowsAsync<InvalidDataException>(() => gate.EnsureAllowedAsync(new ExecutionVersionStamp(5, 6)));
    }

    [TestMethod]
    public async Task EnsureAllowedAsync_AllowsExactOrNewerEpochs()
    {
        var anchor = new InMemoryFloorAnchor(new ExecutionVersionFloor(5, 7));
        var gate = new MonotonicExecutionVersionGate(anchor, "runtime:default");
        Assert.AreEqual(new ExecutionVersionFloor(5, 7), await gate.EnsureAllowedAsync(new ExecutionVersionStamp(5, 7)));
        Assert.AreEqual(new ExecutionVersionFloor(5, 7), await gate.EnsureAllowedAsync(new ExecutionVersionStamp(8, 9)));
    }

    [TestMethod]
    public async Task AdvanceFloorAsync_ProvisionsFromExplicitEmptyCasAndThenAllowsExecution()
    {
        var anchor = new InMemoryFloorAnchor();
        var gate = new MonotonicExecutionVersionGate(anchor, "runtime:default");
        var next = new ExecutionVersionFloor(3, 4);
        var committed = await gate.AdvanceFloorAsync(next);
        Assert.AreEqual(next, committed);
        Assert.AreEqual(1, anchor.AdvanceCount);
        Assert.AreEqual(ExecutionVersionFloorForTest.Empty, anchor.LastExpectedPrevious);
        Assert.AreEqual(next, await gate.EnsureAllowedAsync(new ExecutionVersionStamp(3, 4)));
    }

    [TestMethod]
    public async Task AdvanceFloorAsync_RejectsDecreaseInEitherDimensionWithoutCallingAnchor()
    {
        var anchor = new InMemoryFloorAnchor(new ExecutionVersionFloor(8, 9));
        var gate = new MonotonicExecutionVersionGate(anchor, "runtime:default");
        await Assert.ThrowsAsync<InvalidDataException>(() => gate.AdvanceFloorAsync(new ExecutionVersionFloor(7, 10)));
        await Assert.ThrowsAsync<InvalidDataException>(() => gate.AdvanceFloorAsync(new ExecutionVersionFloor(9, 8)));
        Assert.AreEqual(0, anchor.AdvanceCount);
    }

    [TestMethod]
    public async Task AdvanceFloorAsync_SameFloorIsIdempotentOnlyAfterAnchorProvesIt()
    {
        var existing = new ExecutionVersionFloor(8, 9);
        var anchor = new InMemoryFloorAnchor(existing);
        var gate = new MonotonicExecutionVersionGate(anchor, "runtime:default");
        var committed = await gate.AdvanceFloorAsync(existing);
        Assert.AreEqual(existing, committed);
        Assert.AreEqual(0, anchor.AdvanceCount);
    }

    [TestMethod]
    public async Task AdvanceFloorAsync_FailsWhenAnchorDoesNotCommitExactRequestedFloor()
    {
        var anchor = new InMemoryFloorAnchor { CommitOverride = new ExecutionVersionFloor(99, 99) };
        var gate = new MonotonicExecutionVersionGate(anchor, "runtime:default");
        await Assert.ThrowsAsync<InvalidDataException>(() => gate.AdvanceFloorAsync(new ExecutionVersionFloor(2, 3)));
    }

    [TestMethod]
    public void ConstructorsRejectUnversionedEpochsAndUnsafeScopes()
    {
        Assert.Throws<ArgumentException>(() => new MonotonicExecutionVersionGate(new InMemoryFloorAnchor(), "../escape"));
        Assert.Throws<InvalidDataException>(() => new ExecutionVersionStamp(0, 1).Validate());
        Assert.Throws<InvalidDataException>(() => new ExecutionVersionStamp(1, 0).Validate());
        Assert.Throws<InvalidDataException>(() => new ExecutionVersionFloor(0, 1).Validate());
        Assert.Throws<InvalidDataException>(() => new ExecutionVersionFloor(1, 0).Validate());
    }

    private sealed class InMemoryFloorAnchor : IExecutionVersionFloorAnchor
    {
        private ExecutionVersionFloor? _current;
        public InMemoryFloorAnchor(ExecutionVersionFloor? current = null) { _current = current; }
        public int AdvanceCount { get; private set; }
        public ExecutionVersionFloor? LastExpectedPrevious { get; private set; }
        public ExecutionVersionFloor? CommitOverride { get; init; }

        public Task<ExecutionVersionFloor?> LoadAsync(string scopeId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_current);
        }

        public Task AdvanceAsync(string scopeId, ExecutionVersionFloor expectedPrevious, ExecutionVersionFloor next, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdvanceCount++;
            LastExpectedPrevious = expectedPrevious;
            var effective = _current ?? ExecutionVersionFloorForTest.Empty;
            if (effective != expectedPrevious) throw new InvalidOperationException("stale compare-and-swap");
            _current = CommitOverride ?? next;
            return Task.CompletedTask;
        }
    }

    private static class ExecutionVersionFloorForTest
    {
        public static ExecutionVersionFloor Empty { get; } = new(0, 0);
    }
}
