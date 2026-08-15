using System.Diagnostics;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsJobObjectContainmentTests
{
    [TestMethod]
    public void Policy_RejectsUnsafeLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowsJobObjectPolicy(8_388_608, 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowsJobObjectPolicy(268_435_456, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowsJobObjectPolicy(268_435_456, 65).Validate());
    }

    [TestMethod]
    public async Task Dispose_KillsAssignedLongRunningProcess()
    {
        RequireWindows();
        using var process = StartCommand("for /L %i in (1,1,2147483647) do @set /a a=%i >nul");
        var lease = WindowsJobObjectLease.CreateAndAssign(
            process,
            new WindowsJobObjectPolicy(268_435_456, 1));

        Assert.IsFalse(process.HasExited);
        lease.Dispose();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(process.HasExited);
    }

    [TestMethod]
    public async Task ActiveProcessLimit_BlocksChildProcessCreation()
    {
        RequireWindows();
        var marker = Path.Combine(Path.GetTempPath(), $"aevrix-job-{Guid.NewGuid():N}.txt");
        using var process = StartInteractiveCommand();
        using var lease = WindowsJobObjectLease.CreateAndAssign(
            process,
            new WindowsJobObjectPolicy(268_435_456, 1));

        try
        {
            var child = CommandProcessor().Replace("\"", "\"\"");
            await process.StandardInput.WriteLineAsync($"start /wait \"\" \"{child}\" /d /c \"echo CHILD>{marker}\"");
            await process.StandardInput.WriteLineAsync("exit");
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsFalse(File.Exists(marker), "A child process escaped the active-process Job Object limit.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
    }

    private static Process StartCommand(string command)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CommandProcessor(),
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add(command);
        Assert.IsTrue(process.Start());
        return process;
    }

    private static Process StartInteractiveCommand()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CommandProcessor(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/q");
        Assert.IsTrue(process.Start());
        return process;
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
            Assert.Inconclusive("Windows Job Object integration test requires the Windows CI runner.");
        }
    }
}
