using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class RaceFreeContainmentGateTests
{
    [TestMethod]
    public void Policy_RequiresJobObjectWhenRaceFreeAssignmentIsRequested()
    {
        Assert.Throws<ArgumentException>(() =>
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(2),
                RequireRaceFreeJobAssignment: true).Validate());
    }

    [TestMethod]
    public async Task ExecuteAsync_StrictModeAssignsJobBeforeAdapterRuns()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "race-free-marker.txt");
        var runtime = StrictRuntime(workspace.Path);

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", $"echo STRICT-RACE-FREE>{marker} & type {marker}"],
            workspace.Path));

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(File.Exists(marker));
        StringAssert.Contains(result.StandardOutput, "STRICT-RACE-FREE");
        Assert.IsTrue(result.Attestation.WindowsJobObjectAssigned);
        Assert.IsTrue(result.Attestation.ProcessMemoryLimitEnforced);
        Assert.IsTrue(result.Attestation.ActiveProcessLimitEnforced);
        Assert.IsTrue(result.Attestation.RaceFreeJobAssignmentEnforced);
        Assert.IsTrue(result.Attestation.CpuMemoryLimitsEnforced);
    }

    [TestMethod]
    public async Task ExecuteAsync_StrictModePreservesBoundedOutputAndQuotedArguments()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var runtime = StrictRuntime(workspace.Path);

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", "echo VALUE WITH SPACES"],
            workspace.Path));

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardOutput, "VALUE WITH SPACES");
        Assert.IsTrue(result.Attestation.RaceFreeJobAssignmentEnforced);
    }

    [TestMethod]
    public async Task ExecuteAsync_StrictModeStillEnforcesSingleProcessJobLimit()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "child-should-not-run.txt");
        var child = CommandProcessor().Replace("\"", "\"\"");
        var command = $"\"{child}\" /d /c \"echo CHILD>{marker}\" & if exist \"{marker}\" (echo CHILD-ESCAPED) else echo CHILD-BLOCKED";
        var runtime = StrictRuntime(workspace.Path);

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", command],
            workspace.Path));

        Assert.IsFalse(File.Exists(marker));
        StringAssert.Contains(result.StandardOutput, "CHILD-BLOCKED");
        Assert.IsTrue(result.Attestation.RaceFreeJobAssignmentEnforced);
    }

    [TestMethod]
    public async Task ExecuteAsync_AttestsPostLaunchJobAssignmentAsNotRaceFree()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var runtime = new PinnedOutOfProcessRuntime(
            Descriptor(CommandProcessor()),
            workspace.Path,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(3),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1, 25)));

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", "echo POST-LAUNCH-JOB"],
            workspace.Path));

        Assert.IsTrue(result.Attestation.WindowsJobObjectAssigned);
        Assert.IsFalse(result.Attestation.RaceFreeJobAssignmentEnforced);
    }

    private static PinnedOutOfProcessRuntime StrictRuntime(string workspacePath) =>
        new(
            Descriptor(CommandProcessor()),
            workspacePath,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(5),
                MaximumStdoutBytes: 65_536,
                MaximumStderrBytes: 65_536,
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1, 25),
                RequireRaceFreeJobAssignment: true));

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
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows CI runner required.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-race-free-gate-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
