using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Aevrix.Core;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("AEVRIX readiness soak requires Windows.");
    return 64;
}

var options = Options.Parse(args);
if (!File.Exists(options.EngineHostPath))
{
    Console.Error.WriteLine($"EngineHost not found: {options.EngineHostPath}");
    return 66;
}

var startedAt = DateTimeOffset.UtcNow;
var stopwatch = Stopwatch.StartNew();
var latenciesMs = new List<double>(options.Iterations);
var samples = new List<ResourceSample>();
var failures = new List<string>();
var observedPids = new HashSet<int>();
var restartCount = 0;

await using var supervisor = new EngineHostSupervisor(
    options.EngineHostPath,
    startupTimeout: TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
    requestTimeout: TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

try
{
    await supervisor.StartAsync();
    restartCount++;

    for (var iteration = 1; iteration <= options.Iterations; iteration++)
    {
        if (iteration > 1 && (iteration - 1) % options.RestartEvery == 0)
        {
            var previousPid = supervisor.ProcessId;
            await supervisor.StopAsync();
            if (previousPid is int pid && !WaitForExit(pid, options.ProcessExitTimeoutSeconds))
            {
                failures.Add($"restart:{iteration}: previous EngineHost PID {pid} remained alive");
                break;
            }

            await supervisor.StartAsync();
            restartCount++;
        }

        var processId = supervisor.ProcessId;
        if (processId is null)
        {
            failures.Add($"iteration:{iteration}: supervisor reported no running EngineHost PID");
            break;
        }
        observedPids.Add(processId.Value);

        var requestId = $"soak-{iteration:D6}-{Guid.NewGuid():N}";
        var requestTimer = Stopwatch.StartNew();
        try
        {
            var response = await supervisor.SendAsync(new EnginePingCommand(requestId));
            requestTimer.Stop();
            latenciesMs.Add(requestTimer.Elapsed.TotalMilliseconds);

            if (!response.Success
                || !string.Equals(response.Code, "pong", StringComparison.Ordinal)
                || !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            {
                failures.Add(
                    $"iteration:{iteration}: ping fail-closed response " +
                    $"success={response.Success}, code={response.Code}, requestId={response.RequestId}");
                break;
            }
        }
        catch (Exception ex)
        {
            requestTimer.Stop();
            failures.Add($"iteration:{iteration}: {ex.GetType().Name}: {ex.Message}");
            break;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            process.Refresh();
            var sample = new ResourceSample(
                iteration,
                processId.Value,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.HandleCount,
                process.TotalProcessorTime.TotalMilliseconds);
            samples.Add(sample);

            if (sample.WorkingSetBytes > options.MaxWorkingSetBytes)
            {
                failures.Add(
                    $"iteration:{iteration}: working set {ToMiB(sample.WorkingSetBytes):F2} MiB " +
                    $"exceeded {ToMiB(options.MaxWorkingSetBytes):F2} MiB limit");
                break;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"iteration:{iteration}: unable to sample EngineHost process: {ex.GetType().Name}: {ex.Message}");
            break;
        }
    }
}
catch (Exception ex)
{
    failures.Add($"supervisor:{ex.GetType().Name}: {ex.Message}");
}
finally
{
    var finalPid = supervisor.ProcessId;
    try
    {
        await supervisor.StopAsync();
    }
    catch (Exception ex)
    {
        failures.Add($"shutdown:{ex.GetType().Name}: {ex.Message}");
    }

    if (finalPid is int pid && !WaitForExit(pid, options.ProcessExitTimeoutSeconds))
    {
        failures.Add($"shutdown: EngineHost PID {pid} remained alive after StopAsync");
    }
}

stopwatch.Stop();

foreach (var pid in observedPids)
{
    if (IsAlive(pid))
    {
        failures.Add($"cleanup: observed EngineHost PID {pid} remained alive after soak");
    }
}

var segments = samples
    .GroupBy(sample => sample.ProcessId)
    .OrderBy(group => group.Min(sample => sample.Iteration))
    .Select(group =>
    {
        var ordered = group.OrderBy(sample => sample.Iteration).ToArray();
        var first = ordered[0];
        var last = ordered[^1];
        return new SegmentSummary(
            group.Key,
            first.Iteration,
            last.Iteration,
            ordered.Length,
            first.PrivateMemoryBytes,
            last.PrivateMemoryBytes,
            Math.Max(0, last.PrivateMemoryBytes - first.PrivateMemoryBytes),
            ordered.Max(sample => sample.WorkingSetBytes),
            ordered.Max(sample => sample.PrivateMemoryBytes),
            ordered.Max(sample => sample.HandleCount));
    })
    .ToArray();

var maxSegmentPrivateGrowth = segments.Length == 0 ? 0 : segments.Max(segment => segment.PrivateMemoryGrowthBytes);
if (maxSegmentPrivateGrowth > options.MaxPrivateGrowthBytes)
{
    failures.Add(
        $"maximum same-process private memory growth {ToMiB(maxSegmentPrivateGrowth):F2} MiB exceeded " +
        $"{ToMiB(options.MaxPrivateGrowthBytes):F2} MiB limit");
}

var completedIterations = latenciesMs.Count;
var sortedLatencies = latenciesMs.OrderBy(value => value).ToArray();
var engineHash = await Sha256FileAsync(options.EngineHostPath);
var passed = failures.Count == 0 && completedIterations == options.Iterations;

var report = new
{
    schemaVersion = 2,
    generatedAtUtc = DateTimeOffset.UtcNow,
    startedAtUtc = startedAt,
    engineHostPath = Path.GetFullPath(options.EngineHostPath),
    engineHostSha256 = engineHash,
    requestedIterations = options.Iterations,
    completedIterations,
    restartEvery = options.RestartEvery,
    restartCount,
    durationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
    pass = passed,
    failures,
    latencyMilliseconds = new
    {
        min = sortedLatencies.Length == 0 ? 0 : sortedLatencies[0],
        average = sortedLatencies.Length == 0 ? 0 : sortedLatencies.Average(),
        p50 = Percentile(sortedLatencies, 0.50),
        p95 = Percentile(sortedLatencies, 0.95),
        p99 = Percentile(sortedLatencies, 0.99),
        max = sortedLatencies.Length == 0 ? 0 : sortedLatencies[^1]
    },
    resources = new
    {
        sampleCount = samples.Count,
        maxWorkingSetBytes = samples.Count == 0 ? 0 : samples.Max(sample => sample.WorkingSetBytes),
        maxPrivateMemoryBytes = samples.Count == 0 ? 0 : samples.Max(sample => sample.PrivateMemoryBytes),
        maxHandleCount = samples.Count == 0 ? 0 : samples.Max(sample => sample.HandleCount),
        maxSameProcessPrivateGrowthBytes = maxSegmentPrivateGrowth,
        configuredMaxWorkingSetBytes = options.MaxWorkingSetBytes,
        configuredMaxPrivateGrowthBytes = options.MaxPrivateGrowthBytes
    },
    observedProcessIds = observedPids.OrderBy(pid => pid).ToArray(),
    segments,
    samples
};

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
await File.WriteAllTextAsync(options.OutputPath, json + Environment.NewLine);
Console.WriteLine(json);

return passed ? 0 : 1;

static async Task<string> Sha256FileAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var digest = await SHA256.HashDataAsync(stream);
    return Convert.ToHexString(digest).ToLowerInvariant();
}

static bool WaitForExit(int pid, int timeoutSeconds)
{
    var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    while (DateTime.UtcNow < deadline)
    {
        if (!IsAlive(pid))
        {
            return true;
        }
        Thread.Sleep(50);
    }
    return !IsAlive(pid);
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

static double Percentile(double[] sorted, double percentile)
{
    if (sorted.Length == 0)
    {
        return 0;
    }
    var index = (sorted.Length - 1) * percentile;
    var lower = (int)Math.Floor(index);
    var upper = (int)Math.Ceiling(index);
    if (lower == upper)
    {
        return sorted[lower];
    }
    var fraction = index - lower;
    return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
}

static double ToMiB(long bytes) => bytes / 1024d / 1024d;

internal sealed record ResourceSample(
    int Iteration,
    int ProcessId,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int HandleCount,
    double TotalProcessorTimeMilliseconds);

internal sealed record SegmentSummary(
    int ProcessId,
    int FirstIteration,
    int LastIteration,
    int SampleCount,
    long FirstPrivateMemoryBytes,
    long LastPrivateMemoryBytes,
    long PrivateMemoryGrowthBytes,
    long MaxWorkingSetBytes,
    long MaxPrivateMemoryBytes,
    int MaxHandleCount);

internal sealed record Options(
    string EngineHostPath,
    string OutputPath,
    int Iterations,
    int RestartEvery,
    int StartupTimeoutSeconds,
    int RequestTimeoutSeconds,
    int ProcessExitTimeoutSeconds,
    long MaxWorkingSetBytes,
    long MaxPrivateGrowthBytes)
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

        int PositiveInt(string name, int fallback)
        {
            if (!values.TryGetValue(name, out var raw))
            {
                return fallback;
            }
            if (!int.TryParse(raw, out var value) || value <= 0)
            {
                throw new ArgumentException($"--{name} must be a positive integer.");
            }
            return value;
        }

        long MiB(string name, int fallbackMiB)
        {
            var value = PositiveInt(name, fallbackMiB);
            return checked((long)value * 1024 * 1024);
        }

        var iterations = PositiveInt("iterations", 250);
        var restartEvery = PositiveInt("restart-every", 50);
        if (restartEvery > iterations)
        {
            restartEvery = iterations;
        }

        return new Options(
            Required("enginehost"),
            Required("output"),
            iterations,
            restartEvery,
            PositiveInt("startup-timeout-seconds", 20),
            PositiveInt("request-timeout-seconds", 5),
            PositiveInt("process-exit-timeout-seconds", 5),
            MiB("max-working-set-mib", 512),
            MiB("max-private-growth-mib", 128));
    }
}
