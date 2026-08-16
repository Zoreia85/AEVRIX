using System.Security.Cryptography;
using System.Runtime.Versioning;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class RestrictedTokenProcessRuntimeTests
{
    [TestMethod]
    public void Policy_RejectsRestrictedTokenOutsideRaceFreeWindowsPath()
    {
        Assert.Throws<ArgumentException>(() =>
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(5),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1),
                RequireRestrictedToken: true).Validate());
    }

    [TestMethod]
    public async Task ExecuteAsync_RunsPinnedAdapterUnderVerifiedRestrictedPrimaryToken()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var runtime = new PinnedOutOfProcessRuntime(
            Descriptor(CommandProcessor()),
            workspace.Path,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(5),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1, 25),
                RequireRaceFreeJobAssignment: true,
                RequireRestrictedToken: true));

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", "echo AEVRIX-RESTRICTED-TOKEN-OK"],
            workspace.Path));

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardOutput, "AEVRIX-RESTRICTED-TOKEN-OK");
        Assert.IsTrue(result.Attestation.ExecutableHashVerified);
        Assert.IsTrue(result.Attestation.WindowsJobObjectAssigned);
        Assert.IsTrue(result.Attestation.RaceFreeJobAssignmentEnforced);
        Assert.IsTrue(result.Attestation.RestrictedTokenEnforced);
        Assert.IsFalse(result.Attestation.NetworkIsolationEnforced);
        Assert.IsFalse(result.Attestation.FilesystemIsolationEnforced);
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
            Assert.Inconclusive("Restricted-token process launch integration test requires Windows.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-restricted-token-tests",
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
