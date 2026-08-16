using System.ComponentModel;
using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AppContainerPinnedOutOfProcessRuntimeTests
{
    [TestMethod]
    public void Policy_RequiresRaceFreeLaunchForAppContainer()
    {
        Assert.Throws<ArgumentException>(() =>
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(5),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1),
                RequireAppContainer: true).Validate());
    }

    [TestMethod]
    public void Policy_AppContainerRestrictingSidRequiresFullStrictLaunchPrerequisites()
    {
        Assert.Throws<ArgumentException>(() =>
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(5),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1),
                RequireAppContainerRestrictingSid: true).Validate());

        Assert.Throws<ArgumentException>(() =>
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(5),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1),
                RequireRaceFreeJobAssignment: true,
                RequireAppContainer: true,
                RequireAppContainerRestrictingSid: true).Validate());

        var validated = new OutOfProcessExecutionPolicy(
            TimeSpan.FromSeconds(5),
            WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1),
            RequireRaceFreeJobAssignment: true,
            RequireRestrictedToken: true,
            RequireAppContainer: true,
            RequireAppContainerRestrictingSid: true).Validate();

        Assert.IsTrue(validated.RequireAppContainerRestrictingSid);
    }

    [TestMethod]
    public async Task ExecuteAsync_BindsEphemeralAppContainerBeforeResume()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "appcontainer-marker.txt");
        var runtime = new PinnedOutOfProcessRuntime(
            Descriptor(CommandProcessor()),
            workspace.Path,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(10),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1, 25),
                RequireRaceFreeJobAssignment: true,
                RequireAppContainer: true));

        OutOfProcessExecutionResult result;
        try
        {
            result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
                ["/d", "/c", $"echo APPCONTAINER-OK>{marker} & echo APPCONTAINER-OK"],
                workspace.Path));
        }
        catch (Win32Exception exception)
        {
            Assert.Fail($"AppContainer launch failed with Win32 error {exception.NativeErrorCode}: {exception.Message}");
            throw;
        }

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(File.Exists(marker));
        StringAssert.Contains(result.StandardOutput, "APPCONTAINER-OK");
        Assert.IsTrue(result.Attestation.ExecutableHashVerified);
        Assert.IsTrue(result.Attestation.WindowsJobObjectAssigned);
        Assert.IsTrue(result.Attestation.RaceFreeJobAssignmentEnforced);
        Assert.IsTrue(result.Attestation.AppContainerEnforced);
        Assert.IsFalse(result.Attestation.SandboxRestrictingSidEnforced);
        Assert.IsFalse(result.Attestation.NetworkIsolationEnforced);
        Assert.IsFalse(result.Attestation.FilesystemIsolationEnforced);
    }

    [TestMethod]
    public async Task ExecuteAsync_StrictRestrictingSidIsVerifiedBeforeResumeWithoutClaimingFilesystemIsolation()
    {
        RequireWindows();
        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "restricting-sid-marker.txt");
        var runtime = new PinnedOutOfProcessRuntime(
            Descriptor(CommandProcessor()),
            workspace.Path,
            new OutOfProcessExecutionPolicy(
                TimeSpan.FromSeconds(10),
                WindowsJobObject: new WindowsJobObjectPolicy(268_435_456, 1, 25),
                RequireRaceFreeJobAssignment: true,
                RequireRestrictedToken: true,
                RequireAppContainer: true,
                RequireAppContainerRestrictingSid: true));

        OutOfProcessExecutionResult result;
        try
        {
            result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
                ["/d", "/c", $"echo RESTRICTING-SID-OK>{marker} & echo RESTRICTING-SID-OK"],
                workspace.Path));
        }
        catch (Win32Exception exception)
        {
            Assert.Fail($"Strict AppContainer restricting-SID launch failed with Win32 error {exception.NativeErrorCode}: {exception.Message}");
            throw;
        }

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(File.Exists(marker));
        StringAssert.Contains(result.StandardOutput, "RESTRICTING-SID-OK");
        Assert.IsTrue(result.Attestation.ExecutableHashVerified);
        Assert.IsTrue(result.Attestation.WindowsJobObjectAssigned);
        Assert.IsTrue(result.Attestation.RaceFreeJobAssignmentEnforced);
        Assert.IsTrue(result.Attestation.RestrictedTokenEnforced);
        Assert.IsTrue(result.Attestation.AppContainerEnforced);
        Assert.IsTrue(result.Attestation.SandboxRestrictingSidEnforced);
        Assert.IsFalse(result.Attestation.NetworkIsolationEnforced);
        Assert.IsFalse(result.Attestation.FilesystemIsolationEnforced);
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
            Assert.Inconclusive("AppContainer runtime integration test requires the Windows CI runner.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-appcontainer-runtime-tests",
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
