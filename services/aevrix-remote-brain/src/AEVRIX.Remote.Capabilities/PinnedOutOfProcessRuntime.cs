using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Capabilities;

public sealed record PinnedExecutableDescriptor(string ExecutablePath, string Sha256)
{
    public PinnedExecutableDescriptor Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathFullyQualified(ExecutablePath))
        {
            throw new ArgumentException("Executable path must be absolute.", nameof(ExecutablePath));
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
    IReadOnlyList<string>? InheritedEnvironmentKeys = null)
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

        var keys = InheritedEnvironmentKeys ?? DefaultInheritedEnvironmentKeys;
        if (keys.Count > 32
            || keys.Any(key => string.IsNullOrWhiteSpace(key)
                || key.Length > 128
                || key.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))))
        {
            throw new ArgumentException("Inherited environment key allowlist is invalid.", nameof(InheritedEnvironmentKeys));
        }

        return this;
    }

    internal static IReadOnlyList<string> DefaultInheritedEnvironmentKeys { get; } =
        ["SystemRoot", "WINDIR", "TEMP", "TMP"];
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

        if (string.IsNullOrWhiteSpace(WorkingDirectory) || !Path.IsPathFullyQualified(WorkingDirectory))
        {
            throw new ArgumentException("Working directory must be absolute.", nameof(WorkingDirectory));
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
    bool NetworkIsolationEnforced,
    bool CpuMemoryLimitsEnforced);

public sealed record OutOfProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Runtime,
    OutOfProcessExecutionAttestation Attestation);

/// <summary>
/// Executes one explicitly pinned binary outside the AEVRIX process. This runtime enforces
/// executable hash verification, workspace containment, bounded stdout/stderr, a minimal
/// environment and process-tree termination on timeout/cancellation. It deliberately does
/// not claim network or CPU/memory isolation; those require a stronger container/VM runtime.
/// </summary>
public sealed class PinnedOutOfProcessRuntime
{
    private readonly PinnedExecutableDescriptor _executable;
    private readonly string _workspaceRoot;
    private readonly OutOfProcessExecutionPolicy _policy;

    public PinnedOutOfProcessRuntime(
        PinnedExecutableDescriptor executable,
        string workspaceRoot,
        OutOfProcessExecutionPolicy policy)
    {
        _executable = (executable ?? throw new ArgumentNullException(nameof(executable))).Validate();
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy))).Validate();
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot))
        {
            throw new ArgumentException("Workspace root must be absolute.", nameof(workspaceRoot));
        }

        _workspaceRoot = NormalizeDirectory(workspaceRoot);
    }

    public async Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var workingDirectory = EnsureContainedWorkspace(request.WorkingDirectory);
        VerifyExecutableHash();

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(_executable.ExecutablePath),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyMinimalEnvironment(startInfo, request.Environment);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var started = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            throw new InvalidOperationException("Pinned adapter process could not be started.");
        }

        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runtimeCancellation.CancelAfter(_policy.MaximumRuntime);

        try
        {
            var stdout = ReadBoundedAsync(
                process.StandardOutput.BaseStream,
                _policy.MaximumStdoutBytes,
                process,
                runtimeCancellation.Token);
            var stderr = ReadBoundedAsync(
                process.StandardError.BaseStream,
                _policy.MaximumStderrBytes,
                process,
                runtimeCancellation.Token);
            var wait = process.WaitForExitAsync(runtimeCancellation.Token);

            await Task.WhenAll(wait, stdout, stderr).ConfigureAwait(false);
            return new OutOfProcessExecutionResult(
                process.ExitCode,
                Encoding.UTF8.GetString(stdout.Result),
                Encoding.UTF8.GetString(stderr.Result),
                Stopwatch.GetElapsedTime(started),
                new OutOfProcessExecutionAttestation(
                    ExecutableHashVerified: true,
                    ProcessTreeKillEnforced: true,
                    WorkspaceContainmentVerified: true,
                    EnvironmentAllowlistApplied: true,
                    NetworkIsolationEnforced: false,
                    CpuMemoryLimitsEnforced: false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillTree(process);
            await ObserveExitAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            KillTree(process);
            await ObserveExitAsync(process).ConfigureAwait(false);
            throw new TimeoutException("Pinned adapter process exceeded its governed runtime.", exception);
        }
        catch
        {
            KillTree(process);
            await ObserveExitAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private void VerifyExecutableHash()
    {
        var path = Path.GetFullPath(_executable.ExecutablePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Pinned adapter executable was not found.", path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Pinned adapter executable cannot be a reparse point.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(_executable.Sha256)))
        {
            throw new InvalidDataException("Pinned adapter executable hash does not match the approved SHA-256.");
        }
    }

    private string EnsureContainedWorkspace(string candidate)
    {
        if (!Directory.Exists(_workspaceRoot))
        {
            throw new DirectoryNotFoundException("Governed workspace root does not exist.");
        }

        var full = NormalizeDirectory(candidate);
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(full, _workspaceRoot, comparer)
            && !full.StartsWith(_workspaceRoot + Path.DirectorySeparatorChar, comparer))
        {
            throw new UnauthorizedAccessException("Process working directory is outside the governed workspace.");
        }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException("Process working directory does not exist.");
        }

        var cursor = new DirectoryInfo(full);
        while (cursor is not null)
        {
            if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Governed workspace path cannot traverse a reparse point.");
            }

            if (string.Equals(NormalizeDirectory(cursor.FullName), _workspaceRoot, comparer))
            {
                return full;
            }

            cursor = cursor.Parent;
        }

        throw new UnauthorizedAccessException("Process working directory escaped the governed workspace root.");
    }

    private void ApplyMinimalEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? requested)
    {
        var inheritedKeys = _policy.InheritedEnvironmentKeys ?? OutOfProcessExecutionPolicy.DefaultInheritedEnvironmentKeys;
        var inherited = inheritedKeys
            .Select(key => (Key: key, Value: System.Environment.GetEnvironmentVariable(key)))
            .Where(item => item.Value is not null)
            .ToArray();

        startInfo.Environment.Clear();
        foreach (var item in inherited)
        {
            startInfo.Environment[item.Key] = item.Value!;
        }

        if (requested is null)
        {
            return;
        }

        foreach (var pair in requested)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        Process process,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                KillTree(process);
                throw new InvalidDataException("Pinned adapter process output exceeded its governed byte budget.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
