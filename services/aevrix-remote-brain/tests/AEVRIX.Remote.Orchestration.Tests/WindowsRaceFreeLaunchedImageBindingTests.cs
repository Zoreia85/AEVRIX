using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsRaceFreeLaunchedImageBindingTests
{
    [TestMethod]
    public void Start_MismatchedAuthenticatedIdentity_TerminatesBeforeAdapterRuns()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var workspace = new TempDirectory();
        var marker = Path.Combine(workspace.Path, "must-not-exist.txt");
        var executable = CommandProcessor();
        var unrelatedPath = Path.Combine(workspace.Path, "unrelated.bin");
        File.WriteAllBytes(unrelatedPath, RandomNumberGenerator.GetBytes(32));

        using var unrelated = new FileStream(
            unrelatedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var wrongIdentity = WindowsFileIdentity.FromHandle(unrelated.SafeFileHandle);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value)) environment[key] = value;
        }

        Assert.Throws<InvalidDataException>(() =>
            WindowsRaceFreeProcessLauncher.Start(
                executable,
                ["/d", "/c", $"echo SHOULD-NOT-RUN>{marker}"],
                workspace.Path,
                environment,
                new WindowsJobObjectPolicy(268_435_456, 1, 25),
                authenticatedImageIdentity: wrongIdentity));

        Assert.IsFalse(File.Exists(marker), "The adapter executed before launched-image identity verification completed.");
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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-gate9-binding-tests",
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
