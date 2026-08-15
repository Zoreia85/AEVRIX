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
    public async Task ExecuteAsync_FailsClosedBeforeLaunchWhenRaceFreeAssignmentIsRequired()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "should-not-exist.txt");
        var runtime = new PinnedOutOfProcessRuntime(
            Descriptor(CommandProcessor()),
            workspace.Path,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(3),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1, 25),
                RequireRaceFreeJobAssignment: true));

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                ["/d", "/c", $"echo LAUNCHED>{marker}"],
                workspace.Path)));

        Assert.IsFalse(File.Exists(marker), "Strict containment must fail before adapter code can execute.");
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
            Assert.Inconclusive("Race-free containment runtime test requires the Windows CI runner.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-race-free-gate-tests",
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
