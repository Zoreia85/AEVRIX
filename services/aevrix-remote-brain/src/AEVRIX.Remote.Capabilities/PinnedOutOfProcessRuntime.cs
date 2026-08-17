using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Capabilities;

public sealed record PinnedExecutableDescriptor(string ExecutablePath, string Sha256)
{
    public PinnedExecutableDescriptor Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)
            || !Path.IsPathFullyQualified(ExecutablePath)
            || ExecutablePath.Length > 2_048)
        {
            throw new ArgumentException("Executable path must be a bounded absolute path.", nameof(ExecutablePath));
        }

        if (string.IsNullOrWhiteSpace(Sha256)
            || Sha256.Length != 64
            || !Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Executable SHA-256 is invalid.", nameof(Sha256));
        }

        return this;
    }
}

public sealed record OutOfProcessExecutionPolicy(
    TimeSpan MaximumRuntime,
    int MaximumStdoutBytes = 1_048_576,
    int MaximumStderrBytes = 262_144,
    IReadOnlyList<string>? InheritedEnvironmentKeys = null,
    IReadOnlyList<string>? AllowedRequestedEnvironmentKeys = null,
    WindowsJobObjectPolicy? WindowsJobObject = null,
    bool RequireRaceFreeJobAssignment = false,
    bool RequireRestrictedToken = false,
    bool RequireAppContainer = false)
{
    public OutOfProcessExecutionPolicy Validate()
    {
        if (MaximumRuntime < TimeSpan.FromMilliseconds(100)
            || MaximumRuntime > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRuntime));
        }

        if (MaximumStdoutBytes is < 0 or > 16_777_216
            || MaximumStderrBytes is < 0 or > 4_194_304)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStdoutBytes));
        }

        ValidateEnvironmentKeys(InheritedEnvironmentKeys ?? DefaultInheritedEnvironmentKeys, nameof(InheritedEnvironmentKeys));
        ValidateEnvironmentKeys(AllowedRequestedEnvironmentKeys ?? DefaultAllowedRequestedEnvironmentKeys, nameof(AllowedRequestedEnvironmentKeys));
        WindowsJobObject?.Validate();

        if (RequireRaceFreeJobAssignment && WindowsJobObject is null)
        {
            throw new ArgumentException("Race-free Job Object assignment requires a Windows Job Object policy.", nameof(RequireRaceFreeJobAssignment));
        }

        if (RequireRestrictedToken && !RequireRaceFreeJobAssignment)
        {
            throw new ArgumentException(
                "Restricted-token process launch is available only through the strict race-free Windows launcher.",
                nameof(RequireRestrictedToken));
        }

        if (RequireAppContainer && !RequireRaceFreeJobAssignment)
        {
            throw new ArgumentException(
                "AppContainer process launch is available only through the strict race-free Windows launcher.",
                nameof(RequireAppContainer));
        }

        return this;
    }

    internal static IReadOnlyList<string> DefaultInheritedEnvironmentKeys { get; } = ["SystemRoot", "WINDIR", "TEMP", "TMP"];
    internal static IReadOnlyList<string> DefaultAllowedRequestedEnvironmentKeys { get; } = Array.Empty<string>();

    private static void ValidateEnvironmentKeys(IReadOnlyList<string> keys, string parameterName)
    {
        if (keys.Count > 32
            || keys.Any(key => string.IsNullOrWhiteSpace(key)
                || key.Length > 128
                || key.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
            || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count)
        {
            throw new ArgumentException("Environment key allowlist is invalid.", parameterName);
        }
    }
}

public sealed record OutOfProcessExecutionRequest(
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public OutOfProcessExecutionRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Arguments);
        if (Arguments.Count > 256
            || Arguments.Any(argument => argument is null || argument.Length > 8_192 || argument.Any(char.IsControl)))
        {
            throw new ArgumentException("Process arguments exceed safe bounds.", nameof(Arguments));
        }

        if (string.IsNullOrWhiteSpace(WorkingDirectory)
            || !Path.IsPathFullyQualified(WorkingDirectory)
            || WorkingDirectory.Length > 2_048)
        {
            throw new ArgumentException("Working directory must be a bounded absolute path.", nameof(WorkingDirectory));
        }

        if (Environment is { Count: > 64 }
            || Environment?.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key.Length > 128
                || pair.Key.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))
                || pair.Value is null
                || pair.Value.Length > 8_192
                || pair.Value.Any(char.IsControl)) == true)
        {
            throw new ArgumentException("Process environment exceeds safe bounds.", nameof(Environment));
        }

        return this;
    }
}

public sealed record OutOfProcessExecutionAttestation(
    bool ExecutableHashVerified,
    bool ProcessTreeKillEnforced,
    bool WorkspaceContainmentVerified,
    bool EnvironmentAllowlistApplied,
    bool WindowsJobObjectAssigned,
    bool ProcessMemoryLimitEnforced,
    bool ActiveProcessLimitEnforced,
    bool RaceFreeJobAssignmentEnforced,
    bool NetworkIsolationEnforced,
    bool CpuMemoryLimitsEnforced,
    bool FilesystemIsolationEnforced,
    bool RestrictedTokenEnforced = false,
    bool AppContainerEnforced = false,
    bool LaunchedImageIdentityVerified = false);

public sealed record OutOfProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Runtime,
    OutOfProcessExecutionAttestation Attestation);

/// <summary>
/// Executes one explicitly pinned binary outside the AEVRIX process. This runtime enforces
/// executable hash verification, governed working-directory containment, bounded stdout/stderr,
/// a minimal environment, closed stdin and process-tree termination on timeout/cancellation.
/// On Windows, strict workloads are created suspended, optionally under a reduced primary token
/// and/or an ephemeral AppContainer profile, assigned to the configured Job Object, bound to the
/// stable identity of the same executable object that passed SHA-256 verification, and resumed only
/// after those controls and launched-image identity verification succeed.
/// </summary>
public sealed class PinnedOutOfProcessRuntime
{
    private readonly PinnedExecutableDescriptor _executable;
    private readonly string _workspaceRoot;
    private readonly OutOfProcessExecutionPolicy _policy;

    public PinnedOutOfProcessRuntime(PinnedExecutableDescriptor executable, string workspaceRoot, OutOfProcessExecutionPolicy policy)
    {
        _executable = (executable ?? throw new ArgumentNullException(nameof(executable))).Validate();
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy))).Validate();
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot) || workspaceRoot.Length > 2_048)
        {
            throw new ArgumentException("Workspace root must be a bounded absolute path.", nameof(workspaceRoot));
        }
        _workspaceRoot = NormalizeDirectory(workspaceRoot);
    }

    public async Task<OutOfProcessExecutionResult> ExecuteAsync(OutOfProcessExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var workingDirectory = EnsureContainedWorkspace(request.WorkingDirectory);
        ValidateRequestedEnvironment(request.Environment);
        using var executableLease = VerifyExecutableHashAndLock();

        if (_policy.RequireRaceFreeJobAssignment)
        {
            return await ExecuteRaceFreeWindowsAsync(request, workingDirectory, executableLease, cancellationToken).ConfigureAwait(false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(_executable.ExecutablePath),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        ApplyMinimalEnvironment(startInfo, request.Environment);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var started = Stopwatch.GetTimestamp();
        if (!process.Start()) throw new InvalidOperationException("Pinned adapter process could not be started.");

        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runtimeCancellation.CancelAfter(_policy.MaximumRuntime);
        WindowsJobObjectLease? jobLease = null;
        try
        {
            if (_policy.WindowsJobObject is not null) jobLease = WindowsJobObjectLease.CreateAndAssign(process, _policy.WindowsJobObject);
            process.StandardInput.Close();
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, _policy.MaximumStdoutBytes, process, runtimeCancellation.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, _policy.MaximumStderrBytes, process, runtimeCancellation.Token);
            var wait = process.WaitForExitAsync(runtimeCancellation.Token);
            await Task.WhenAll(wait, stdout, stderr).ConfigureAwait(false);
            return new OutOfProcessExecutionResult(
                process.ExitCode,
                Encoding.UTF8.GetString(stdout.Result),
                Encoding.UTF8.GetString(stderr.Result),
                Stopwatch.GetElapsedTime(started),
                new OutOfProcessExecutionAttestation(
                    true, true, true, true,
                    jobLease is not null,
                    jobLease is not null,
                    jobLease is not null,
                    false,
                    false,
                    jobLease?.CpuRateLimitEnforced == true,
                    false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillTree(process); await ObserveExitAsync(process).ConfigureAwait(false); throw;
        }
        catch (OperationCanceledException exception)
        {
            KillTree(process); await ObserveExitAsync(process).ConfigureAwait(false);
            throw new TimeoutException("Pinned adapter process exceeded its governed runtime.", exception);
        }
        catch
        {
            KillTree(process); await ObserveExitAsync(process).ConfigureAwait(false); throw;
        }
        finally { jobLease?.Dispose(); }
    }

    private async Task<OutOfProcessExecutionResult> ExecuteRaceFreeWindowsAsync(
        OutOfProcessExecutionRequest request,
        string workingDirectory,
        VerifiedExecutableLease executableLease,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Race-free Job Object assignment requires Windows.");
        }

        var jobPolicy = _policy.WindowsJobObject
            ?? throw new InvalidOperationException("Race-free Job Object assignment requires a Job Object policy.");
        var authenticatedImageIdentity = executableLease.WindowsIdentity
            ?? throw new InvalidOperationException("Strict Windows launch requires authenticated executable file identity.");
        var environment = BuildMinimalEnvironment(request.Environment);
        var started = Stopwatch.GetTimestamp();
        using var restrictedToken = _policy.RequireRestrictedToken
            ? WindowsRestrictedTokenLease.Create()
            : null;
        using var appContainer = _policy.RequireAppContainer
            ? WindowsAppContainerProfileLease.Create()
            : null;
        using var appContainerWorkspaceAcl = appContainer is not null
            ? WindowsSandboxWorkspaceAclLease.Create(
                workingDirectory,
                appContainer.AppContainerSid,
                SandboxWorkspaceAccess.ReadWrite)
            : null;
        using var launch = WindowsRaceFreeProcessLauncher.Start(
            Path.GetFullPath(_executable.ExecutablePath),
            request.Arguments,
            workingDirectory,
            environment,
            jobPolicy,
            restrictedToken,
            appContainer,
            authenticatedImageIdentity);
        var process = launch.Process;

        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runtimeCancellation.CancelAfter(_policy.MaximumRuntime);
        try
        {
            var stdout = ReadBoundedAsync(launch.StandardOutput, _policy.MaximumStdoutBytes, process, runtimeCancellation.Token);
            var stderr = ReadBoundedAsync(launch.StandardError, _policy.MaximumStderrBytes, process, runtimeCancellation.Token);
            var wait = process.WaitForExitAsync(runtimeCancellation.Token);
            await Task.WhenAll(wait, stdout, stderr).ConfigureAwait(false);
            return new OutOfProcessExecutionResult(
                process.ExitCode,
                Encoding.UTF8.GetString(stdout.Result),
                Encoding.UTF8.GetString(stderr.Result),
                Stopwatch.GetElapsedTime(started),
                new OutOfProcessExecutionAttestation(
                    true, true, true, true,
                    true, true, true, true,
                    false,
                    launch.JobLease.CpuRateLimitEnforced,
                    false,
                    launch.RestrictedTokenEnforced,
                    launch.AppContainerEnforced,
                    launch.LaunchedImageIdentityVerified));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillTree(process); await ObserveExitAsync(process).ConfigureAwait(false); throw;
        }
        catch (OperationCanceledException exception)
        {
            KillTree(process); await ObserveExitAsync(process).ConfigureAwait(false);
            throw new TimeoutException("Pinned adapter process exceeded its governed runtime.", exception);
        }
        catch
        {
            KillTree(process); await ObserveExitAsync(process).ConfigureAwait(false); throw;
        }
    }

    private VerifiedExecutableLease VerifyExecutableHashAndLock()
    {
        var path = Path.GetFullPath(_executable.ExecutablePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Pinned adapter executable was not found.", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Pinned adapter executable cannot be a reparse point.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actualHash = SHA256.HashData(stream);
            var expectedHash = Convert.FromHexString(_executable.Sha256);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash)) throw new InvalidDataException("Pinned adapter executable hash does not match the approved SHA-256.");
            }
            finally { CryptographicOperations.ZeroMemory(actualHash); CryptographicOperations.ZeroMemory(expectedHash); }
            return VerifiedExecutableLease.FromVerifiedStream(stream);
        }
        catch { stream.Dispose(); throw; }
    }

    private string EnsureContainedWorkspace(string candidate)
    {
        if (!Directory.Exists(_workspaceRoot)) throw new DirectoryNotFoundException("Governed workspace root does not exist.");
        var full = NormalizeDirectory(candidate);
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(full, _workspaceRoot, comparer) && !full.StartsWith(_workspaceRoot + Path.DirectorySeparatorChar, comparer)) throw new UnauthorizedAccessException("Process working directory is outside the governed workspace.");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Process working directory does not exist.");
        var cursor = new DirectoryInfo(full);
        while (cursor is not null)
        {
            if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Governed workspace path cannot traverse a reparse point.");
            if (string.Equals(NormalizeDirectory(cursor.FullName), _workspaceRoot, comparer)) return full;
            cursor = cursor.Parent;
        }
        throw new UnauthorizedAccessException("Process working directory escaped the governed workspace root.");
    }

    private void ValidateRequestedEnvironment(IReadOnlyDictionary<string, string>? requested)
    {
        if (requested is null || requested.Count == 0) return;
        var allowed = (_policy.AllowedRequestedEnvironmentKeys ?? OutOfProcessExecutionPolicy.DefaultAllowedRequestedEnvironmentKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Keys.Any(key => !allowed.Contains(key))) throw new UnauthorizedAccessException("Requested process environment contains keys outside the explicit allowlist.");
    }

    private Dictionary<string, string> BuildMinimalEnvironment(IReadOnlyDictionary<string, string>? requested)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inheritedKeys = _policy.InheritedEnvironmentKeys ?? OutOfProcessExecutionPolicy.DefaultInheritedEnvironmentKeys;
        foreach (var key in inheritedKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value is not null) result[key] = value;
        }
        if (requested is not null)
        {
            foreach (var pair in requested) result[pair.Key] = pair.Value;
        }
        return result;
    }

    private void ApplyMinimalEnvironment(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? requested)
    {
        var environment = BuildMinimalEnvironment(requested);
        startInfo.Environment.Clear();
        foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, Process process, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maximumBytes) { KillTree(process); throw new InvalidDataException("Pinned adapter process output exceeded its governed byte budget."); }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
    }

    private static string NormalizeDirectory(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
