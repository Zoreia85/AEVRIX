using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class GovernedOutOfProcessRuntimeTests
{
    [TestMethod]
    public async Task ExecuteAsync_RejectsNetworkAndFilesystemIsolationBeforeLaunch()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "unified-gate-marker.txt");
        var runtime = Runtime(
            workspace.Path,
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));

        var decision = runtime.EvaluateAuthority();
        Assert.IsFalse(decision.LaunchAuthorized);
        Assert.AreEqual("NetworkAndFilesystemIsolationBackendUnavailable", decision.DecisionCode);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                ["/d", "/c", $"echo SHOULD-NOT-RUN>{marker}"],
                workspace.Path)));

        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsNetworkOnlyIsolationBeforeLaunch()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "network-only-marker.txt");
        var runtime = Runtime(
            workspace.Path,
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.LoopbackOnly),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted));

        Assert.AreEqual("NetworkIsolationBackendUnavailable", runtime.EvaluateAuthority().DecisionCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(["/d", "/c", $"echo SHOULD-NOT-RUN>{marker}"], workspace.Path)));
        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsFilesystemOnlyIsolationBeforeLaunch()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "filesystem-only-marker.txt");
        var runtime = Runtime(
            workspace.Path,
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.Unrestricted),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceReadOnly));

        Assert.AreEqual("FilesystemIsolationBackendUnavailable", runtime.EvaluateAuthority().DecisionCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(["/d", "/c", $"echo SHOULD-NOT-RUN>{marker}"], workspace.Path)));
        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    public async Task ExecuteAsync_UnrestrictedAuthorityDelegatesWithoutClaimingIsolation()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var runtime = Runtime(
            workspace.Path,
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.Unrestricted),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted));

        var decision = runtime.EvaluateAuthority();
        Assert.IsTrue(decision.LaunchAuthorized);
        Assert.AreEqual("AuthorizedUnrestrictedLocalProcess", decision.DecisionCode);
        Assert.AreEqual("local-unrestricted", decision.SelectedBackendId);

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", "echo UNIFIED-AUTHORITY-GATE"],
            workspace.Path));

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardOutput, "UNIFIED-AUTHORITY-GATE");
        Assert.IsFalse(result.Attestation.NetworkIsolationEnforced);
        Assert.IsFalse(result.Attestation.FilesystemIsolationEnforced);
    }

    [TestMethod]
    public void EvaluateAuthority_SelectsHighestPriorityCompatibleBackend()
    {
        var low = new StubIsolationBackend("restricted-low", 10, canEnforce: true, networkEnforced: true, filesystemEnforced: true);
        var high = new StubIsolationBackend("restricted-high", 20, canEnforce: true, networkEnforced: true, filesystemEnforced: true);
        var runtime = new GovernedOutOfProcessRuntime(
            [low, high],
            RestrictedAuthority());

        var decision = runtime.EvaluateAuthority();

        Assert.IsTrue(decision.LaunchAuthorized);
        Assert.AreEqual("AuthorizedByIsolationBackend", decision.DecisionCode);
        Assert.AreEqual("restricted-high", decision.SelectedBackendId);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsBackendThatClaimsIsolationWithoutAttestation()
    {
        using var workspace = new TempDirectory();
        var backend = new StubIsolationBackend(
            "misleading-backend",
            100,
            canEnforce: true,
            networkEnforced: false,
            filesystemEnforced: true);
        var runtime = new GovernedOutOfProcessRuntime([backend], RestrictedAuthority());

        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest([], workspace.Path)));
        Assert.AreEqual(1, backend.ExecutionCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_AllowsRestrictedBackendOnlyWhenAttestationProvesAuthority()
    {
        using var workspace = new TempDirectory();
        var backend = new StubIsolationBackend(
            "attested-backend",
            100,
            canEnforce: true,
            networkEnforced: true,
            filesystemEnforced: true);
        var runtime = new GovernedOutOfProcessRuntime([backend], RestrictedAuthority());

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest([], workspace.Path));

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(result.Attestation.NetworkIsolationEnforced);
        Assert.IsTrue(result.Attestation.FilesystemIsolationEnforced);
        Assert.AreEqual(1, backend.ExecutionCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsWriteBoundaryWithoutReadIsolation()
    {
        using var workspace = new TempDirectory();
        var backend = new StubIsolationBackend(
            "write-only-proof",
            100,
            canEnforce: true,
            networkEnforced: true,
            filesystemEnforced: true,
            filesystemWriteBoundaryEnforced: true,
            filesystemReadIsolationEnforced: false);
        var runtime = new GovernedOutOfProcessRuntime([backend], RestrictedAuthority());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest([], workspace.Path)));

        StringAssert.Contains(error.Message, "external-read");
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsReadIsolationWithoutWriteBoundary()
    {
        using var workspace = new TempDirectory();
        var backend = new StubIsolationBackend(
            "read-only-proof",
            100,
            canEnforce: true,
            networkEnforced: true,
            filesystemEnforced: true,
            filesystemWriteBoundaryEnforced: false,
            filesystemReadIsolationEnforced: true);
        var runtime = new GovernedOutOfProcessRuntime([backend], RestrictedAuthority());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest([], workspace.Path)));

        StringAssert.Contains(error.Message, "external-write");
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsAuthorityBindingFromDifferentPolicy()
    {
        using var workspace = new TempDirectory();
        var stalePolicy = new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.LoopbackOnly),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));
        var backend = new StubIsolationBackend(
            "stale-attestation",
            100,
            true,
            true,
            true,
            authorityFingerprintOverride: stalePolicy.ComputeFingerprint());
        var runtime = new GovernedOutOfProcessRuntime([backend], RestrictedAuthority());

        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest([], workspace.Path)));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsAuthorityBindingFromDifferentBackend()
    {
        using var workspace = new TempDirectory();
        var backend = new StubIsolationBackend(
            "selected-backend",
            100,
            true,
            true,
            true,
            backendIdOverride: "other-backend");
        var runtime = new GovernedOutOfProcessRuntime([backend], RestrictedAuthority());

        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest([], workspace.Path)));
    }

    [TestMethod]
    public void ComputeFingerprint_IsDeterministicAndSensitiveToAuthority()
    {
        var one = new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(
                OutOfProcessNetworkScope.Allowlisted,
                [new NetworkEndpointRule("LOCALHOST", 443), new NetworkEndpointRule("api.example.test", 8443)]),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));
        var reordered = new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(
                OutOfProcessNetworkScope.Allowlisted,
                [new NetworkEndpointRule("api.example.test", 8443), new NetworkEndpointRule("localhost", 443)]),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));
        var changed = reordered with
        {
            Filesystem = new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceReadOnly)
        };

        Assert.AreEqual(one.ComputeFingerprint(), reordered.ComputeFingerprint());
        Assert.AreNotEqual(one.ComputeFingerprint(), changed.ComputeFingerprint());
    }

    [TestMethod]
    public void Constructor_RejectsDuplicateBackendIds()
    {
        var one = new StubIsolationBackend("duplicate-backend", 1, true, true, true);
        var two = new StubIsolationBackend("duplicate-backend", 2, true, true, true);

        Assert.Throws<ArgumentException>(() =>
            new GovernedOutOfProcessRuntime([one, two], RestrictedAuthority()));
    }

    [TestMethod]
    public void Policy_ValidatesBothAuthorities()
    {
        Assert.Throws<ArgumentException>(() =>
            new OutOfProcessAuthorityPolicy(
                new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.Allowlisted),
                new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted)).Validate());
    }

    private static OutOfProcessAuthorityPolicy RestrictedAuthority() =>
        new(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));

    private static GovernedOutOfProcessRuntime Runtime(
        string workspaceRoot,
        OutOfProcessNetworkPolicy network,
        OutOfProcessFilesystemPolicy filesystem) =>
        new(
            new PinnedOutOfProcessRuntime(
                Descriptor(CommandProcessor()),
                workspaceRoot,
                new OutOfProcessExecutionPolicy(TimeSpan.FromSeconds(5))),
            new OutOfProcessAuthorityPolicy(network, filesystem));

    private static PinnedExecutableDescriptor Descriptor(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new PinnedExecutableDescriptor(path, hash);
    }

    private static string CommandProcessor()
    {
        var path = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        }
        return Path.GetFullPath(path);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Governed process runtime integration test requires the Windows CI runner.");
        }
    }

    private sealed class StubIsolationBackend(
        string backendId,
        int priority,
        bool canEnforce,
        bool networkEnforced,
        bool filesystemEnforced,
        string? backendIdOverride = null,
        string? authorityFingerprintOverride = null,
        bool filesystemWriteBoundaryEnforced = true,
        bool filesystemReadIsolationEnforced = true) : IOutOfProcessIsolationBackend
    {
        public string BackendId => backendId;
        public int Priority => priority;
        public int ExecutionCount { get; private set; }

        public bool CanEnforce(OutOfProcessAuthorityPolicy authority) => canEnforce;

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
                    true,
                    true,
                    true,
                    true,
                    false,
                    false,
                    false,
                    false,
                    networkEnforced,
                    false,
                    filesystemEnforced)));
        }

        public IsolationAuthorityAttestation AttestAuthority(
            OutOfProcessAuthorityPolicy authority,
            OutOfProcessExecutionResult execution) =>
            new(
                backendIdOverride ?? BackendId,
                authorityFingerprintOverride ?? authority.ComputeFingerprint(),
                filesystemWriteBoundaryEnforced,
                filesystemReadIsolationEnforced);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-unified-authority-tests",
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
