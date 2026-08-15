using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class PinnedOutOfProcessRuntimeTests
{
    [TestMethod]
    public async Task ExecuteAsync_VerifiesPinnedBinaryAndCapturesBoundedOutput()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var runtime = Runtime(workspace.Path, TimeSpan.FromSeconds(5));

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", "echo AEVRIX-RUNTIME-OK"],
            workspace.Path));

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardOutput, "AEVRIX-RUNTIME-OK");
        Assert.IsTrue(result.Attestation.ExecutableHashVerified);
        Assert.IsTrue(result.Attestation.ProcessTreeKillEnforced);
        Assert.IsTrue(result.Attestation.WorkspaceContainmentVerified);
        Assert.IsTrue(result.Attestation.EnvironmentAllowlistApplied);
        Assert.IsFalse(result.Attestation.NetworkIsolationEnforced);
        Assert.IsFalse(result.Attestation.CpuMemoryLimitsEnforced);
    }

    [TestMethod]
    public async Task ExecuteAsync_KillsLongRunningProcessTreeOnTimeout()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var runtime = Runtime(workspace.Path, TimeSpan.FromMilliseconds(250));
        var started = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<TimeoutException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                ["/d", "/c", "for /L %i in (1,1,2147483647) do @set /a a=%i >nul"],
                workspace.Path)));

        Assert.IsTrue(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ExecuteAsync_KillsProcessWhenOutputBudgetIsExceeded()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var descriptor = Descriptor(CommandProcessor());
        var runtime = new PinnedOutOfProcessRuntime(
            descriptor,
            workspace.Path,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(5),
                MaximumStdoutBytes: 64,
                MaximumStderrBytes: 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                ["/d", "/c", "for /L %i in (1,1,200) do @echo 1234567890"],
                workspace.Path)));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsExecutableHashMismatchBeforeLaunch()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var runtime = new PinnedOutOfProcessRuntime(
            new PinnedExecutableDescriptor(CommandProcessor(), new string('0', 64)),
            workspace.Path,
            new OutOfProcessExecutionPolicy(TimeSpan.FromSeconds(2)));

        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(["/d", "/c", "echo SHOULD-NOT-RUN"], workspace.Path)));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsWorkingDirectoryOutsideGovernedWorkspace()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        using var outside = new TempDirectory();
        var runtime = Runtime(workspace.Path, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(["/d", "/c", "echo SHOULD-NOT-RUN"], outside.Path)));
    }

    [TestMethod]
    public async Task ExecuteAsync_DoesNotLeakUnallowlistedParentEnvironment()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        const string key = "AEVRIX_RUNTIME_SECRET_REGRESSION";
        const string value = "must-not-reach-child";
        var before = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);

        try
        {
            var runtime = Runtime(workspace.Path, TimeSpan.FromSeconds(3));
            var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
                ["/d", "/c", $"set {key}"],
                workspace.Path));

            Assert.IsFalse(result.StandardOutput.Contains(value, StringComparison.Ordinal));
            Assert.IsFalse(result.StandardError.Contains(value, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, before);
        }
    }

    private static PinnedOutOfProcessRuntime Runtime(string workspaceRoot, TimeSpan timeout) =>
        new(
            Descriptor(CommandProcessor()),
            workspaceRoot,
            new OutOfProcessExecutionPolicy(timeout));

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
            Assert.Inconclusive("Pinned process runtime integration test requires the Windows CI runner.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-process-runtime-tests",
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
