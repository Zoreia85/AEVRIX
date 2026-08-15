using System.Runtime.Versioning;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsAppContainerRaceFreeLaunchTests
{
    [TestMethod]
    public async Task Start_BindsVerifiedAppContainerBeforeChildExecutes()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("AppContainer launch verification requires Windows.");
            return;
        }

        var executable = CommandProcessor();
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using var profile = WindowsAppContainerProfileLease.Create();
        using var launch = WindowsRaceFreeProcessLauncher.Start(
            executable,
            ["/d", "/c", "echo AEVRIX-APPCONTAINER-OK"],
            systemDirectory,
            MinimalEnvironment(),
            new WindowsJobObjectPolicy(268_435_456, 1),
            appContainerProfile: profile);

        using var reader = new StreamReader(launch.StandardOutput);
        var stdout = await reader.ReadToEndAsync();
        await launch.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, launch.Process.ExitCode);
        Assert.IsTrue(launch.AppContainerEnforced);
        Assert.IsTrue(launch.JobLease.ProcessMemoryLimitEnforced);
        Assert.IsTrue(launch.JobLease.ActiveProcessLimitEnforced);
        StringAssert.Contains(stdout, "AEVRIX-APPCONTAINER-OK");
    }

    private static IReadOnlyDictionary<string, string> MinimalEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value)) result[key] = value;
        }
        return result;
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
}
