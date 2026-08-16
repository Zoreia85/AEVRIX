using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Aevrix.Core;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("AEVRIX EngineHost recovery probe requires Windows.");
    return 64;
}

var options = Options.Parse(args);
if (!File.Exists(options.EngineHostPath))
{
    Console.Error.WriteLine($"EngineHost not found: {options.EngineHostPath}");
    return 66;
}

var failures = new List<string>();
var firstPingPassed = false;
var crashObserved = false;
var failClosedAfterCrash = false;
var restartPassed = false;
var secondPingPassed = false;
var cleanupVerified = false;
int? firstPid = null;
int? secondPid = null;

await using var supervisor = new EngineHostSupervisor(
    options.EngineHostPath,
    startupTimeout: TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
    requestTimeout: TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

try
{
    await supervisor.StartAsync();
    firstPid = supervisor.ProcessId;
    if (firstPid is null)
    {
        failures.Add("Initial EngineHost PID was not available after authenticated startup.");
    }
    else
    {
        firstPingPassed = await PingAsync(supervisor, "recovery-before-crash");
        if (!firstPingPassed)
        {
            failures.Add("Initial authenticated ping failed.");
        }

        using (var external = Process.GetProcessById(firstPid.Value))
        {
            external.Kill(entireProcessTree: true);
            using var crashTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.ProcessExitTimeoutSeconds));
            try
            {
                await external.WaitForExitAsync(crashTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                failures.Add($"Forced-crash EngineHost PID {firstPid.Value} did not exit within the timeout.");
            }
        }

        crashObserved = !IsAlive(firstPid.Value) && !supervisor.IsRunning && supervisor.ProcessId is null;
        if (!crashObserved)
        {
            failures.Add("Supervisor did not revoke running state after the EngineHost process was killed.");
        }

        try
        {
            _ = await supervisor.SendAsync(new EnginePingCommand($"after-crash-{Guid.NewGuid():N}"));
            failures.Add("SendAsync unexpectedly succeeded after EngineHost crash.");
        }
        catch (InvalidOperationException)
        {
            failClosedAfterCrash = true;
        }
        catch (Exception ex)
        {
            failures.Add($"Unexpected post-crash exception {ex.GetType().Name}: {ex.Message}");
        }

        await supervisor.StartAsync();
        secondPid = supervisor.ProcessId;
        restartPassed = secondPid is int pid && pid != firstPid.Value && supervisor.IsRunning;
        if (!restartPassed)
        {
            failures.Add("EngineHost did not restart under a distinct authenticated process identity.");
        }
        else
        {
            secondPingPassed = await PingAsync(supervisor, "recovery-after-restart");
            if (!secondPingPassed)
            {
                failures.Add("Authenticated ping after recovery restart failed.");
            }
        }
    }
}
catch (Exception ex)
{
    failures.Add($"Recovery probe exception {ex.GetType().Name}: {ex.Message}");
}
finally
{
    try
    {
        await supervisor.StopAsync();
    }
    catch (Exception ex)
    {
        failures.Add($"Final supervisor cleanup failed: {ex.GetType().Name}: {ex.Message}");
    }

    var firstGone = firstPid is null || !IsAlive(firstPid.Value);
    var secondGone = secondPid is null || !IsAlive(secondPid.Value);
    cleanupVerified = firstGone && secondGone && !supervisor.IsRunning && supervisor.ProcessId is null;
    if (!cleanupVerified)
    {
        failures.Add("One or more observed EngineHost processes remained alive after final cleanup.");
    }
}

var engineHash = await Sha256FileAsync(options.EngineHostPath);
var passed = failures.Count == 0
    && firstPingPassed
    && crashObserved
    && failClosedAfterCrash
    && restartPassed
    && secondPingPassed
    && cleanupVerified;

var report = new
{
    schemaVersion = 1,
    generatedAtUtc = DateTimeOffset.UtcNow,
    engineHostPath = Path.GetFullPath(options.EngineHostPath),
    engineHostSha256 = engineHash,
    pass = passed,
    firstProcessId = firstPid,
    secondProcessId = secondPid,
    firstPingPassed,
    crashObserved,
    failClosedAfterCrash,
    restartPassed,
    secondPingPassed,
    cleanupVerified,
    failures,
    scope = "Forced EngineHost process crash, fail-closed post-crash authority, authenticated restart and process cleanup. This does not prove full Desktop/downstream recovery."
};

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
await File.WriteAllTextAsync(options.OutputPath, json + Environment.NewLine);
Console.WriteLine(json);
return passed ? 0 : 1;

static async Task<bool> PingAsync(EngineHostSupervisor supervisor, string prefix)
{
    var requestId = $"{prefix}-{Guid.NewGuid():N}";
    var response = await supervisor.SendAsync(new EnginePingCommand(requestId));
    return response.Success
        && string.Equals(response.Code, "pong", StringComparison.Ordinal)
        && string.Equals(response.RequestId, requestId, StringComparison.Ordinal);
}

static async Task<string> Sha256FileAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var digest = await SHA256.HashDataAsync(stream);
    return Convert.ToHexString(digest).ToLowerInvariant();
}

static bool IsAlive(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

internal sealed record Options(
    string EngineHostPath,
    string OutputPath,
    int StartupTimeoutSeconds,
    int RequestTimeoutSeconds,
    int ProcessExitTimeoutSeconds)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Arguments must be --name value pairs.");
            }
            values[args[i][2..]] = args[i + 1];
        }

        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{name} is required.");

        int Positive(string name, int fallback)
        {
            if (!values.TryGetValue(name, out var raw))
            {
                return fallback;
            }
            if (!int.TryParse(raw, out var parsed) || parsed <= 0)
            {
                throw new ArgumentException($"--{name} must be a positive integer.");
            }
            return parsed;
        }

        return new Options(
            Required("enginehost"),
            Required("output"),
            Positive("startup-timeout-seconds", 20),
            Positive("request-timeout-seconds", 5),
            Positive("process-exit-timeout-seconds", 5));
    }
}
