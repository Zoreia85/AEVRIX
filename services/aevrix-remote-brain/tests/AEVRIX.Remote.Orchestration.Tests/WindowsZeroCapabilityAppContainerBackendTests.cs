using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsZeroCapabilityAppContainerBackendTests
{
    [TestMethod]
    public async Task ExecuteAsync_ProvesNetworkNoneByBlockingLoopbackConnection()
    {
        RequireWindows();
        var curl = CurlExecutable();
        if (!File.Exists(curl))
        {
            Assert.Inconclusive("Windows curl.exe is unavailable on this runner.");
            return;
        }

        using var workspace = new TempDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var runtime = new PinnedOutOfProcessRuntime(
            Descriptor(curl),
            workspace.Path,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(8),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1, 25),
                RequireRaceFreeJobAssignment: true,
                RequireAppContainer: true));
        var authority = NetworkNoneAuthority();
        var governed = new GovernedOutOfProcessRuntime(
            [new WindowsZeroCapabilityAppContainerBackend(runtime, loopbackPolicy: new StubLoopbackInspector(0))],
            authority);

        var result = await governed.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["--silent", "--show-error", "--connect-timeout", "2", "--max-time", "3", $"http://127.0.0.1:{port}/"],
            workspace.Path));

        Assert.AreNotEqual(0, result.ExitCode, "A zero-capability AppContainer unexpectedly reached loopback.");
        Assert.IsTrue(result.Attestation.AppContainerEnforced);
        Assert.IsTrue(result.Attestation.NetworkIsolationEnforced);
        Assert.IsFalse(result.Attestation.FilesystemIsolationEnforced);

        using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var accepted = false;
        try
        {
            using var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
            accepted = true;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.IsFalse(accepted, "The AppContainer established a loopback TCP connection despite Network=None authority.");
    }

    [TestMethod]
    public async Task ExecuteAsync_BlocksBeforeLaunchWhenAnyGlobalLoopbackExemptionExists()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "should-not-exist.txt");
        var runtime = new PinnedOutOfProcessRuntime(
            Descriptor(CommandProcessor()),
            workspace.Path,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(5),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1),
                RequireRaceFreeJobAssignment: true,
                RequireAppContainer: true));
        var backend = new WindowsZeroCapabilityAppContainerBackend(
            runtime,
            loopbackPolicy: new StubLoopbackInspector(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ExecuteAsync(
            NetworkNoneAuthority(),
            new OutOfProcessExecutionRequest(["/d", "/c", $"echo launched>\"{marker}\""], workspace.Path)));

        Assert.IsFalse(File.Exists(marker), "Network=None backend launched a process despite a configured loopback exemption.");
    }

    [TestMethod]
    public void NativeLoopbackInspector_ReturnsBoundedNonNegativeCount()
    {
        RequireWindows();
        var count = new WindowsAppContainerLoopbackPolicyInspector().GetLoopbackExemptionCount();
        Assert.IsTrue(count >= 0);
        Assert.IsTrue(count <= 65_536, "Windows returned an implausibly large AppContainer loopback exemption table.");
    }

    [TestMethod]
    public void CanEnforce_AcceptsOnlyNetworkNoneWithUnrestrictedFilesystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows AppContainer backend selection requires Windows.");
            return;
        }

        using var workspace = new TempDirectory();
        var backend = new WindowsZeroCapabilityAppContainerBackend(
            new PinnedOutOfProcessRuntime(
                Descriptor(CommandProcessor()),
                workspace.Path,
                new OutOfProcessExecutionPolicy(
                    TimeSpan.FromSeconds(5),
                    WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1),
                    RequireRaceFreeJobAssignment: true,
                    RequireAppContainer: true)),
            loopbackPolicy: new StubLoopbackInspector(0));

        Assert.IsTrue(backend.CanEnforce(NetworkNoneAuthority()));
        Assert.IsFalse(backend.CanEnforce(new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.LoopbackOnly),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted))));
        Assert.IsFalse(backend.CanEnforce(new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly))));
    }

    private static OutOfProcessAuthorityPolicy NetworkNoneAuthority() => new(
        new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
        new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted));

    private static PinnedExecutableDescriptor Descriptor(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new PinnedExecutableDescriptor(
            path,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private static string CurlExecutable() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");

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
            Assert.Inconclusive("Zero-capability AppContainer network isolation test requires Windows.");
        }
    }

    private sealed class StubLoopbackInspector(int count) : IAppContainerLoopbackPolicyInspector
    {
        public int GetLoopbackExemptionCount() => count;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-appcontainer-network-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
