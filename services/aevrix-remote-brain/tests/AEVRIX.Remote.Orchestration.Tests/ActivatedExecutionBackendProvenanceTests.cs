using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class ActivatedExecutionBackendProvenanceTests
{
    [TestMethod]
    public async Task ExecuteWithProvenanceAsync_ReportsValidatedActivatedBackendAndCapabilities()
    {
        using var workspace = new TempDirectory();
        var authority = RestrictedAuthority();
        var backend = new ProvenanceStubBackend(
            "attested-backend",
            priority: 50,
            networkIsolationEnforced: true,
            filesystemIsolationEnforced: true,
            filesystemWriteBoundaryEnforced: true,
            filesystemReadIsolationEnforced: true,
            restrictedTokenEnforced: true,
            appContainerEnforced: true,
            launchedImageIdentityVerified: true);
        var runtime = new GovernedOutOfProcessRuntime([backend], authority);

        var result = await runtime.ExecuteWithProvenanceAsync(
            new OutOfProcessExecutionRequest([], workspace.Path));

        Assert.AreEqual(0, result.Execution.ExitCode);
        Assert.AreEqual(1, backend.ExecutionCount);
        Assert.AreEqual("attested-backend", result.Provenance.BackendId);
        Assert.AreEqual(authority.ComputeFingerprint(), result.Provenance.AuthorityFingerprint);
        Assert.IsTrue(result.Provenance.NetworkIsolationEnforced);
        Assert.IsTrue(result.Provenance.FilesystemIsolationEnforced);
        Assert.IsTrue(result.Provenance.FilesystemWriteBoundaryEnforced);
        Assert.IsTrue(result.Provenance.FilesystemReadIsolationEnforced);
        Assert.IsTrue(result.Provenance.RestrictedTokenEnforced);
        Assert.IsTrue(result.Provenance.AppContainerEnforced);
        Assert.IsTrue(result.Provenance.LaunchedImageIdentityVerified);
    }

    [TestMethod]
    public async Task ExecuteWithProvenanceAsync_RejectsBackendIdentityMismatchBeforeReturningProvenance()
    {
        using var workspace = new TempDirectory();
        var backend = new ProvenanceStubBackend(
            "selected-backend",
            priority: 50,
            networkIsolationEnforced: true,
            filesystemIsolationEnforced: true,
            filesystemWriteBoundaryEnforced: true,
            filesystemReadIsolationEnforced: true,
            backendIdOverride: "other-backend");
        var runtime = new GovernedOutOfProcessRuntime([backend], RestrictedAuthority());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runtime.ExecuteWithProvenanceAsync(new OutOfProcessExecutionRequest([], workspace.Path)));
        Assert.AreEqual(1, backend.ExecutionCount);
    }

    [TestMethod]
    public async Task ExecuteWithProvenanceAsync_RejectsStaleAuthorityFingerprintBeforeReturningProvenance()
    {
        using var workspace = new TempDirectory();
        var staleAuthority = new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.LoopbackOnly),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));
        var backend = new ProvenanceStubBackend(
            "stale-policy-backend",
            priority: 50,
            networkIsolationEnforced: true,
            filesystemIsolationEnforced: true,
            filesystemWriteBoundaryEnforced: true,
            filesystemReadIsolationEnforced: true,
            authorityFingerprintOverride: staleAuthority.ComputeFingerprint());
        var runtime = new GovernedOutOfProcessRuntime([backend], RestrictedAuthority());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runtime.ExecuteWithProvenanceAsync(new OutOfProcessExecutionRequest([], workspace.Path)));
        Assert.AreEqual(1, backend.ExecutionCount);
    }

    private static OutOfProcessAuthorityPolicy RestrictedAuthority() =>
        new(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));

    private sealed class ProvenanceStubBackend(
        string backendId,
        int priority,
        bool networkIsolationEnforced,
        bool filesystemIsolationEnforced,
        bool filesystemWriteBoundaryEnforced,
        bool filesystemReadIsolationEnforced,
        bool restrictedTokenEnforced = false,
        bool appContainerEnforced = false,
        bool launchedImageIdentityVerified = false,
        string? backendIdOverride = null,
        string? authorityFingerprintOverride = null) : IOutOfProcessIsolationBackend
    {
        public string BackendId => backendId;
        public int Priority => priority;
        public int ExecutionCount { get; private set; }

        public bool CanEnforce(OutOfProcessAuthorityPolicy authority) => true;

        public Task<OutOfProcessExecutionResult> ExecuteAsync(
            OutOfProcessAuthorityPolicy authority,
            OutOfProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(new OutOfProcessExecutionResult(
                0,
                "stub",
                string.Empty,
                TimeSpan.FromMilliseconds(1),
                new OutOfProcessExecutionAttestation(
                    ExecutableHashVerified: true,
                    ProcessTreeKillEnforced: true,
                    WorkspaceContainmentVerified: true,
                    EnvironmentAllowlistApplied: true,
                    WindowsJobObjectAssigned: true,
                    ProcessMemoryLimitEnforced: true,
                    ActiveProcessLimitEnforced: true,
                    RaceFreeJobAssignmentEnforced: true,
                    NetworkIsolationEnforced: networkIsolationEnforced,
                    CpuMemoryLimitsEnforced: true,
                    FilesystemIsolationEnforced: filesystemIsolationEnforced,
                    RestrictedTokenEnforced: restrictedTokenEnforced,
                    AppContainerEnforced: appContainerEnforced,
                    LaunchedImageIdentityVerified: launchedImageIdentityVerified)));
        }

        public IsolationAuthorityAttestation AttestAuthority(
            OutOfProcessAuthorityPolicy authority,
            OutOfProcessExecutionResult execution) =>
            new(
                backendIdOverride ?? BackendId,
                authorityFingerprintOverride ?? authority.ComputeFingerprint(),
                FilesystemWriteBoundaryEnforced: filesystemWriteBoundaryEnforced,
                FilesystemReadIsolationEnforced: filesystemReadIsolationEnforced);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-gate10-provenance-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
