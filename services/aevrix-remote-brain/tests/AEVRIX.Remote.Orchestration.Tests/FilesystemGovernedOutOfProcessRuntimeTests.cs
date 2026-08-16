using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class FilesystemGovernedOutOfProcessRuntimeTests
{
    [TestMethod]
    public async Task ExecuteAsync_RejectsWorkspaceOnlyBeforeLaunchWithoutEnforcementBackend()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "filesystem-gate-marker.txt");
        var runtime = new FilesystemGovernedOutOfProcessRuntime(
            Runtime(workspace.Path),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                ["/d", "/c", $"echo SHOULD-NOT-RUN>{marker}"],
                workspace.Path)));

        Assert.IsFalse(File.Exists(marker), "Filesystem-constrained execution launched without an enforcement backend.");
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsWorkspaceReadOnlyBeforeLaunchWithoutEnforcementBackend()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "filesystem-readonly-marker.txt");
        var runtime = new FilesystemGovernedOutOfProcessRuntime(
            Runtime(workspace.Path),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceReadOnly));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                ["/d", "/c", $"echo SHOULD-NOT-RUN>{marker}"],
                workspace.Path)));

        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    public async Task ExecuteAsync_UnrestrictedDelegatesAndKeepsFilesystemAttestationFalse()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var runtime = new FilesystemGovernedOutOfProcessRuntime(
            Runtime(workspace.Path),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted));

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", "echo FILESYSTEM-GATE-UNRESTRICTED"],
            workspace.Path));

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardOutput, "FILESYSTEM-GATE-UNRESTRICTED");
        Assert.IsTrue(result.Attestation.WorkspaceContainmentVerified);
        Assert.IsFalse(result.Attestation.FilesystemIsolationEnforced);
    }

    [TestMethod]
    public void Policy_RejectsUnknownScope()
    {
        var policy = new OutOfProcessFilesystemPolicy((OutOfProcessFilesystemScope)999);
        Assert.Throws<ArgumentOutOfRangeException>(policy.Validate);
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
            Assert.Inconclusive("Filesystem-governed process runtime integration test requires the Windows CI runner.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-filesystem-runtime-tests",
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
