using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class NetworkGovernedOutOfProcessRuntimeTests
{
    [TestMethod]
    public async Task ExecuteAsync_RejectsNoNetworkScopeBeforeLaunchWithoutEnforcementBackend()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "network-gate-marker.txt");
        var runtime = new NetworkGovernedOutOfProcessRuntime(
            Runtime(workspace.Path),
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                ["/d", "/c", $"echo SHOULD-NOT-RUN>{marker}"],
                workspace.Path)));

        Assert.IsFalse(File.Exists(marker), "Constrained network execution launched without an enforcement backend.");
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsLoopbackOnlyScopeBeforeLaunchWithoutEnforcementBackend()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "loopback-gate-marker.txt");
        var runtime = new NetworkGovernedOutOfProcessRuntime(
            Runtime(workspace.Path),
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.LoopbackOnly));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                ["/d", "/c", $"echo SHOULD-NOT-RUN>{marker}"],
                workspace.Path)));

        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsAllowlistedScopeBeforeLaunchWithoutEnforcementBackend()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "allowlist-gate-marker.txt");
        var runtime = new NetworkGovernedOutOfProcessRuntime(
            Runtime(workspace.Path),
            new OutOfProcessNetworkPolicy(
                OutOfProcessNetworkScope.Allowlisted,
                [new NetworkEndpointRule("127.0.0.1", 11434)]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                ["/d", "/c", $"echo SHOULD-NOT-RUN>{marker}"],
                workspace.Path)));

        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    public async Task ExecuteAsync_UnrestrictedScopeDelegatesAndKeepsNetworkAttestationFalse()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var runtime = new NetworkGovernedOutOfProcessRuntime(
            Runtime(workspace.Path),
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.Unrestricted));

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", "echo NETWORK-GATE-UNRESTRICTED"],
            workspace.Path));

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardOutput, "NETWORK-GATE-UNRESTRICTED");
        Assert.IsFalse(result.Attestation.NetworkIsolationEnforced);
    }

    [TestMethod]
    public void Policy_RejectsMissingOrMisplacedAllowlistAndDuplicates()
    {
        Assert.Throws<ArgumentException>(() =>
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.Allowlisted).Validate());

        Assert.Throws<ArgumentException>(() =>
            new OutOfProcessNetworkPolicy(
                OutOfProcessNetworkScope.None,
                [new NetworkEndpointRule("127.0.0.1", 443)]).Validate());

        Assert.Throws<ArgumentException>(() =>
            new OutOfProcessNetworkPolicy(
                OutOfProcessNetworkScope.Allowlisted,
                [
                    new NetworkEndpointRule("LOCALHOST", 443),
                    new NetworkEndpointRule("localhost", 443)
                ]).Validate());
    }

    [TestMethod]
    public void Endpoint_RejectsInvalidHostAndPort()
    {
        Assert.Throws<ArgumentException>(() => new NetworkEndpointRule("bad host", 443).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkEndpointRule("localhost", 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkEndpointRule("localhost", 65_536).Validate());
    }

    private static PinnedOutOfProcessRuntime Runtime(string workspaceRoot) =>
        new(
            Descriptor(CommandProcessor()),
            workspaceRoot,
            new OutOfProcessExecutionPolicy(TimeSpan.FromSeconds(5)));

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
            Assert.Inconclusive("Network-governed process runtime integration test requires the Windows CI runner.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-network-runtime-tests",
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
