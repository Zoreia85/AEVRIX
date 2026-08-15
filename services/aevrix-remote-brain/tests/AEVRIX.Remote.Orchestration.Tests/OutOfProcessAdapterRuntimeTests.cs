using System.Diagnostics;
using System.Security.Cryptography;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class OutOfProcessAdapterRuntimeTests
{
    [TestMethod]
    public async Task ExecuteAsync_RunsPinnedExecutableWithoutInheritedEnvironment()
    {
        using var workspace = new TemporaryWorkspace();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var runtime = new OutOfProcessAdapterRuntime();

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            executable,
            await Sha256Async(executable),
            ["/d", "/c", "echo AEVRIX_PROCESS_RUNTIME_OK"],
            workspace.Path,
            TimeSpan.FromSeconds(5)));

        Assert.AreEqual(OutOfProcessExecutionOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(result.Stdout.Contains("AEVRIX_PROCESS_RUNTIME_OK", StringComparison.Ordinal));
        Assert.IsFalse(result.ProcessTreeKilled);
    }

    [TestMethod]
    public async Task ExecuteAsync_KillsProcessTreeWhenTimeoutExpires()
    {
        using var workspace = new TemporaryWorkspace();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
        var runtime = new OutOfProcessAdapterRuntime();
        var stopwatch = Stopwatch.StartNew();

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            executable,
            await Sha256Async(executable),
            ["127.0.0.1", "-n", "20", "-w", "1000"],
            workspace.Path,
            TimeSpan.FromMilliseconds(250)));

        stopwatch.Stop();
        Assert.AreEqual(OutOfProcessExecutionOutcome.TimedOut, result.Outcome);
        Assert.IsTrue(result.ProcessTreeKilled);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ExecuteAsync_KillsProcessWhenOutputBudgetIsExceeded()
    {
        using var workspace = new TemporaryWorkspace();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
        var runtime = new OutOfProcessAdapterRuntime();

        var result = await runtime.ExecuteAsync(new OutOfProcessExecutionRequest(
            executable,
            await Sha256Async(executable),
            ["127.0.0.1", "-n", "4", "-w", "1000"],
            workspace.Path,
            TimeSpan.FromSeconds(5),
            MaximumStdoutBytes: 128,
            MaximumStderrBytes: 128));

        Assert.AreEqual(OutOfProcessExecutionOutcome.OutputLimitExceeded, result.Outcome);
        Assert.IsTrue(result.ProcessTreeKilled);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsExecutableWhenPinnedHashDoesNotMatch()
    {
        using var workspace = new TemporaryWorkspace();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var runtime = new OutOfProcessAdapterRuntime();

        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                executable,
                new string('0', 64),
                ["/d", "/c", "exit 0"],
                workspace.Path,
                TimeSpan.FromSeconds(5))));
    }

    [TestMethod]
    public async Task ExecuteAsync_PropagatesCallerCancellation()
    {
        using var workspace = new TemporaryWorkspace();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
        var executableHash = await Sha256Async(executable);
        var runtime = new OutOfProcessAdapterRuntime();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.ExecuteAsync(
            new OutOfProcessExecutionRequest(
                executable,
                executableHash,
                ["127.0.0.1", "-n", "20", "-w", "1000"],
                workspace.Path,
                TimeSpan.FromSeconds(10)),
            cancellation.Token));
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
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
