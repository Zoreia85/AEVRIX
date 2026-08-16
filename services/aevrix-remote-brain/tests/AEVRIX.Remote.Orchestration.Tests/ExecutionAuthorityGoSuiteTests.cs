using System.Diagnostics;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class ExecutionAuthorityGoSuiteTests
{
    [TestMethod]
    [Timeout(180_000)]
    public void CanonicalGoSuite_Passes()
    {
        var service = FindExecutionAuthority();
        var start = new ProcessStartInfo
        {
            FileName = "go",
            WorkingDirectory = service,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add("./...");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to launch the pinned Go execution-authority test suite.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(150_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Execution Authority Go test suite exceeded the 150 second gate.");
        }

        Task.WaitAll(stdout, stderr);
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Execution Authority Go suite failed.{Environment.NewLine}{stdout.Result}{Environment.NewLine}{stderr.Result}");
    }

    private static string FindExecutionAuthority()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 16; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "services", "aevrix-execution-authority");
            if (File.Exists(Path.Combine(candidate, "go.mod")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "Repository root containing services/aevrix-execution-authority/go.mod was not found.");
    }
}
