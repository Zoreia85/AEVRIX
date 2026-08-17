using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class DowngradeResistantGovernedOutOfProcessRuntimeTests
{
    [TestMethod]
    public async Task ExecuteAsync_MissingFloorBlocksBeforeBackendExecution()
    {
        var backend = new CountingBackend();
        var runtime = Runtime(backend, 5, 7, null);
        using var workspace = new TempDirectory();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(new OutOfProcessExecutionRequest([], workspace.Path)));
        Assert.AreEqual(0, backend.ExecutionCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_BackendDowngradeBlocksBeforeBackendExecution()
    {
        var backend = new CountingBackend();
        var runtime = Runtime(backend, 4, 7, new ExecutionVersionFloor(5, 7));
        using var workspace = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(new OutOfProcessExecutionRequest([], workspace.Path)));
        Assert.AreEqual(0, backend.ExecutionCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_PolicyDowngradeBlocksBeforeBackendExecution()
    {
        var backend = new CountingBackend();
        var runtime = Runtime(backend, 5, 6, new ExecutionVersionFloor(5, 7));
        using var workspace = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(new OutOfProcessExecutionRequest([], workspace.Path)));
        Assert.AreEqual(0, backend.ExecutionCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_ExactFloorAllowsOneValidatedBackendExecution()
    {
        var backend = new CountingBackend();
        var runtime = Runtime(backend, 5, 7, new ExecutionVersionFloor(5, 7));
        using var workspace = new TempDirectory();
        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest([], workspace.Path));
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, backend.ExecutionCount);
    }

    [TestMethod]
    public void Constructor_RejectsUnversionedBackendAndPolicyEpoch()
    {
        var backend = new CountingBackend();
        var gate = new MonotonicExecutionVersionGate(new InMemoryFloorAnchor(new ExecutionVersionFloor(1, 1)), "runtime:test");
        Assert.Throws<ArgumentOutOfRangeException>(() => new VersionedIsolationBackendRegistration(backend, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DowngradeResistantGovernedOutOfProcessRuntime([new VersionedIsolationBackendRegistration(backend, 1)], RestrictedAuthority(), 0, gate));
    }

    private static DowngradeResistantGovernedOutOfProcessRuntime Runtime(CountingBackend backend, ulong backendEpoch, ulong policyEpoch, ExecutionVersionFloor? floor)
    {
        var gate = new MonotonicExecutionVersionGate(new InMemoryFloorAnchor(floor), "runtime:test");
        return new DowngradeResistantGovernedOutOfProcessRuntime([new VersionedIsolationBackendRegistration(backend, backendEpoch)], RestrictedAuthority(), policyEpoch, gate);
    }

    private static OutOfProcessAuthorityPolicy RestrictedAuthority() => new(new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None), new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));

    private sealed class CountingBackend : IOutOfProcessIsolationBackend
    {
        public string BackendId => "gate11-counting-backend";
        public int Priority => 100;
        public int ExecutionCount { get; private set; }
        public bool CanEnforce(OutOfProcessAuthorityPolicy authority) => true;
        public Task<OutOfProcessExecutionResult> ExecuteAsync(OutOfProcessAuthorityPolicy authority, OutOfProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(new OutOfProcessExecutionResult(0, "gate11", string.Empty, TimeSpan.FromMilliseconds(1), new OutOfProcessExecutionAttestation(ExecutableHashVerified: true, ProcessTreeKillEnforced: true, WorkspaceContainmentVerified: true, EnvironmentAllowlistApplied: true, WindowsJobObjectAssigned: true, ProcessMemoryLimitEnforced: true, ActiveProcessLimitEnforced: true, RaceFreeJobAssignmentEnforced: true, NetworkIsolationEnforced: true, CpuMemoryLimitsEnforced: true, FilesystemIsolationEnforced: true, RestrictedTokenEnforced: true, AppContainerEnforced: true, LaunchedImageIdentityVerified: true)));
        }
        public IsolationAuthorityAttestation AttestAuthority(OutOfProcessAuthorityPolicy authority, OutOfProcessExecutionResult execution) => new(BackendId, authority.ComputeFingerprint(), FilesystemWriteBoundaryEnforced: true, FilesystemReadIsolationEnforced: true);
    }

    private sealed class InMemoryFloorAnchor(ExecutionVersionFloor? initial) : IExecutionVersionFloorAnchor
    {
        private ExecutionVersionFloor? _current = initial;
        public Task<ExecutionVersionFloor?> LoadAsync(string scopeId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_current);
        }
        public Task AdvanceAsync(string scopeId, ExecutionVersionFloor expectedPrevious, ExecutionVersionFloor next, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effective = _current ?? new ExecutionVersionFloor(0, 0);
            if (effective != expectedPrevious) throw new InvalidOperationException("stale compare-and-swap");
            _current = next;
            return Task.CompletedTask;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-gate11-runtime-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
