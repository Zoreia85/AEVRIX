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

        using var workspace = new TempDirectory("network");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var runtime = Runtime(curl, workspace.Path);
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
    public async Task ExecuteAsync_WorkspaceOnlyAllowsWriteInsideGovernedWorkspace()
    {
        RequireWindows();
        using var workspace = new TempDirectory("inside");
        var marker = Path.Combine(workspace.Path, "inside-marker.txt");
        var governed = GovernedCommandRuntime(workspace.Path, WorkspaceOnlyAuthority());

        var result = await governed.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", $"echo workspace-ok>\"{marker}\""],
            workspace.Path));

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.IsTrue(File.Exists(marker), "WorkspaceOnly AppContainer could not create a file inside its governed workspace.");
        StringAssert.Contains(File.ReadAllText(marker), "workspace-ok");
        Assert.IsTrue(result.Attestation.AppContainerEnforced);
        Assert.IsTrue(result.Attestation.NetworkIsolationEnforced);
        Assert.IsTrue(result.Attestation.FilesystemIsolationEnforced);
    }

    [TestMethod]
    public async Task ExecuteAsync_WorkspaceOnlyDeniesControlledReadAndWriteOutsideWorkspace()
    {
        RequireWindows();
        using var workspace = new TempDirectory("workspace");
        using var outside = new TempDirectory("outside");
        var outsideSentinel = Path.Combine(outside.Path, "outside-sentinel.txt");
        var outsideWrite = Path.Combine(outside.Path, "should-not-be-created.txt");
        File.WriteAllText(outsideSentinel, "aevrix-controlled-outside-sentinel");

        var governed = GovernedCommandRuntime(workspace.Path, WorkspaceOnlyAuthority());

        var read = await governed.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", $"type \"{outsideSentinel}\""],
            workspace.Path));
        Assert.AreNotEqual(0, read.ExitCode, "WorkspaceOnly AppContainer unexpectedly read a controlled file outside the governed workspace.");
        Assert.IsFalse(read.StandardOutput.Contains("aevrix-controlled-outside-sentinel", StringComparison.Ordinal),
            "Outside sentinel content escaped the WorkspaceOnly filesystem boundary.");
        Assert.IsTrue(read.Attestation.FilesystemIsolationEnforced);

        var write = await governed.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", $"echo escaped>\"{outsideWrite}\""],
            workspace.Path));
        Assert.AreNotEqual(0, write.ExitCode, "WorkspaceOnly AppContainer unexpectedly wrote outside the governed workspace.");
        Assert.IsFalse(File.Exists(outsideWrite), "A file was created outside the governed workspace.");
        Assert.IsTrue(write.Attestation.FilesystemIsolationEnforced);
    }

    [TestMethod]
    public async Task ExecuteAsync_BlocksBeforeLaunchWhenAnyGlobalLoopbackExemptionExists()
    {
        RequireWindows();
        using var workspace = new TempDirectory("blocked");
        var marker = Path.Combine(workspace.Path, "should-not-exist.txt");
        var runtime = Runtime(CommandProcessor(), workspace.Path);
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
    public void CanEnforce_AcceptsOnlyNetworkNoneWithSupportedFilesystemScopes()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows AppContainer backend selection requires Windows.");
            return;
        }

        using var workspace = new TempDirectory("selection");
        var backend = new WindowsZeroCapabilityAppContainerBackend(
            Runtime(CommandProcessor(), workspace.Path),
            loopbackPolicy: new StubLoopbackInspector(0));

        Assert.IsTrue(backend.CanEnforce(NetworkNoneAuthority()));
        Assert.IsTrue(backend.CanEnforce(WorkspaceOnlyAuthority()));
        Assert.IsFalse(backend.CanEnforce(new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceReadOnly))));
        Assert.IsFalse(backend.CanEnforce(new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.LoopbackOnly),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly))));
    }

    private static GovernedOutOfProcessRuntime GovernedCommandRuntime(
        string workspaceRoot,
        OutOfProcessAuthorityPolicy authority) =>
        new(
            [new WindowsZeroCapabilityAppContainerBackend(
                Runtime(CommandProcessor(), workspaceRoot),
                loopbackPolicy: new StubLoopbackInspector(0))],
            authority);

    private static PinnedOutOfProcessRuntime Runtime(string executable, string workspaceRoot) =>
        new(
            Descriptor(executable),
            workspaceRoot,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(8),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1, 25),
                RequireRaceFreeJobAssignment: true,
                RequireAppContainer: true));

    private static OutOfProcessAuthorityPolicy NetworkNoneAuthority() => new(
        new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
        new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted));

    private static OutOfProcessAuthorityPolicy WorkspaceOnlyAuthority() => new(
        new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
        new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));

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
            Assert.Inconclusive("Zero-capability AppContainer isolation test requires Windows.");
        }
    }

    private sealed class StubLoopbackInspector(int count) : IAppContainerLoopbackPolicyInspector
    {
        public int GetLoopbackExemptionCount() => count;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string purpose)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-appcontainer-isolation-tests",
                purpose,
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
