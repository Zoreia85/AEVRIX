using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsAppContainerFilesystemBoundaryTests
{
    [TestMethod]
    public async Task AppContainer_AllowsGovernedWorkspaceAndDeniesControlledExternalWrite()
    {
        RequireWindows();
        using var workspace = new TempDirectory("aevrix-appcontainer-fs-workspace");
        using var outside = new TempDirectory("aevrix-appcontainer-fs-outside");

        var insideSource = Path.Combine(workspace.Path, "inside-source.txt");
        var insideCopy = Path.Combine(workspace.Path, "inside-copy.txt");
        var outsideReadObservation = Path.Combine(workspace.Path, "outside-read.txt");
        var outsideSource = Path.Combine(outside.Path, "outside-source.txt");
        var outsideWrite = Path.Combine(outside.Path, "outside-write.txt");
        var batch = Path.Combine(workspace.Path, "probe.cmd");

        await File.WriteAllTextAsync(insideSource, "inside-ok");
        await File.WriteAllTextAsync(outsideSource, "outside-secret");
        await File.WriteAllTextAsync(batch, string.Join("\r\n", new[]
        {
            "@echo off",
            "type \"inside-source.txt\" > \"inside-copy.txt\" || exit /b 10",
            $"type \"{outsideSource}\" >nul 2>&1",
            "if errorlevel 1 goto outside_read_denied",
            "echo allowed>\"outside-read.txt\"",
            "goto after_read",
            ":outside_read_denied",
            "echo denied>\"outside-read.txt\"",
            ":after_read",
            $"echo escaped>\"{outsideWrite}\" 2>nul",
            "exit /b 0"
        }));

        var command = CommandProcessor();
        var runtime = new PinnedOutOfProcessRuntime(
            Descriptor(command),
            workspace.Path,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(8),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1, 25),
                RequireRaceFreeJobAssignment: true,
                RequireAppContainer: true));

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            ["/d", "/c", "probe.cmd"],
            workspace.Path));

        Assert.AreEqual(0, result.ExitCode,
            $"Hostile filesystem write-boundary probe failed with stdout='{result.StandardOutput}' stderr='{result.StandardError}'.");
        Assert.IsTrue(result.Attestation.AppContainerEnforced);
        Assert.IsTrue(result.Attestation.WorkspaceContainmentVerified);
        Assert.IsFalse(result.Attestation.FilesystemIsolationEnforced,
            "External read visibility is not universally denied, so the generic runtime must not claim complete filesystem isolation.");
        Assert.IsTrue(File.Exists(insideCopy), "AppContainer could not write inside the explicitly ACL-granted workspace.");
        Assert.AreEqual("inside-ok", await File.ReadAllTextAsync(insideCopy));
        Assert.IsTrue(File.Exists(outsideReadObservation), "Filesystem probe did not record the external-read observation.");
        var readObservation = (await File.ReadAllTextAsync(outsideReadObservation)).Trim();
        Assert.IsTrue(readObservation is "allowed" or "denied", "External-read observation is invalid.");
        Assert.IsFalse(File.Exists(outsideWrite),
            "AppContainer created a controlled file outside the governed workspace despite the write boundary.");
        Assert.AreEqual("outside-secret", await File.ReadAllTextAsync(outsideSource),
            "External sentinel content changed unexpectedly.");
    }

    private static PinnedExecutableDescriptor Descriptor(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new PinnedExecutableDescriptor(
            path,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
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
            Assert.Inconclusive("Hostile AppContainer filesystem boundary probe requires Windows.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string category)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                category,
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
