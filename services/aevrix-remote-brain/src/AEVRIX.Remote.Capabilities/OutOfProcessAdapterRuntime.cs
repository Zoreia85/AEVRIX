using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Capabilities;

public enum OutOfProcessExecutionOutcome
{
    Succeeded,
    Failed,
    TimedOut,
    OutputLimitExceeded
}

public sealed record OutOfProcessExecutionRequest(
    string ExecutablePath,
    string ExecutableSha256,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaximumStdoutBytes = 256_000,
    int MaximumStderrBytes = 128_000)
{
    public OutOfProcessExecutionRequest Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)
            || !Path.IsPathFullyQualified(ExecutablePath)
            || ExecutablePath.Length > 2_048)
        {
            throw new ArgumentException("Executable path must be a bounded absolute path.", nameof(ExecutablePath));
        }

        if (string.IsNullOrWhiteSpace(ExecutableSha256)
            || ExecutableSha256.Length != 64
            || !ExecutableSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Executable SHA-256 must be 64 hexadecimal characters.", nameof(ExecutableSha256));
        }

        if (Arguments is null
            || Arguments.Count > 256
            || Arguments.Any(argument => argument is null || argument.Length > 8_192))
        {
            throw new ArgumentException("Process arguments exceed safe bounds.", nameof(Arguments));
        }

        if (string.IsNullOrWhiteSpace(WorkingDirectory)
            || !Path.IsPathFullyQualified(WorkingDirectory)
            || WorkingDirectory.Length > 2_048)
        {
            throw new ArgumentException("Working directory must be a bounded absolute path.", nameof(WorkingDirectory));
        }

        if (Timeout < TimeSpan.FromMilliseconds(100) || Timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }

        if (MaximumStdoutBytes is < 128 or > 16_000_000
            || MaximumStderrBytes is < 128 or > 16_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStdoutBytes));
        }

        return this;
    }
}

public sealed record OutOfProcessExecutionResult(
    OutOfProcessExecutionOutcome Outcome,
    int? ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    string ExecutableSha256,
    bool ProcessTreeKilled)
{
    public bool Succeeded => Outcome == OutOfProcessExecutionOutcome.Succeeded && ExitCode == 0;
}

/// <summary>
/// Runs a pinned executable out-of-process without shell expansion, inherited environment variables,
/// stdin, or unbounded stdout/stderr buffering. This provides process lifetime containment and
/// deterministic kill-tree behavior. It does NOT by itself enforce network, filesystem ACL, CPU,
/// memory, token, container, or VM isolation; those remain separate execution-envelope gates.
/// </summary>
public sealed class OutOfProcessAdapterRuntime
{
    public async Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var executablePath = Path.GetFullPath(request.ExecutablePath);
        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Pinned adapter executable was not found.", executablePath);
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException("Adapter working directory does not exist.");
        }

        var actualHash = await ComputeSha256Async(executablePath, cancellationToken).ConfigureAwait(false);
        var actualHashBytes = Convert.FromHexString(actualHash);
        var expectedHashBytes = Convert.FromHexString(request.ExecutableSha256);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes))
            {
                throw new InvalidDataException("Adapter executable hash does not match the pinned SHA-256.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHashBytes);
            CryptographicOperations.ZeroMemory(expectedHashBytes);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.Environment.Clear();
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        var started = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            throw new InvalidOperationException("Adapter process failed to start.");
        }

        process.StandardInput.Close();
        var processTreeKilled = false;
        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            request.MaximumStdoutBytes,
            () => processTreeKilled |= TryKillTree(process),
            CancellationToken.None);
        var stderrTask = ReadBoundedAsync(
            process.StandardError.BaseStream,
            request.MaximumStderrBytes,
            () => processTreeKilled |= TryKillTree(process),
            CancellationToken.None);

        try
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(request.Timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                processTreeKilled |= TryKillTree(process);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException)
            {
                processTreeKilled |= TryKillTree(process);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
                var timedOutOutput = await DrainAfterTerminationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                return new OutOfProcessExecutionResult(
                    OutOfProcessExecutionOutcome.TimedOut,
                    process.HasExited ? process.ExitCode : null,
                    timedOutOutput.Stdout,
                    timedOutOutput.Stderr,
                    Stopwatch.GetElapsedTime(started),
                    actualHash,
                    processTreeKilled);
            }

            try
            {
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                return new OutOfProcessExecutionResult(
                    process.ExitCode == 0
                        ? OutOfProcessExecutionOutcome.Succeeded
                        : OutOfProcessExecutionOutcome.Failed,
                    process.ExitCode,
                    stdout,
                    stderr,
                    Stopwatch.GetElapsedTime(started),
                    actualHash,
                    processTreeKilled);
            }
            catch (OutputLimitExceededException)
            {
                processTreeKilled |= TryKillTree(process);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
                var bounded = await DrainAfterTerminationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                return new OutOfProcessExecutionResult(
                    OutOfProcessExecutionOutcome.OutputLimitExceeded,
                    process.HasExited ? process.ExitCode : null,
                    bounded.Stdout,
                    bounded.Stderr,
                    Stopwatch.GetElapsedTime(started),
                    actualHash,
                    processTreeKilled);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                processTreeKilled |= TryKillTree(process);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        Action onOverflow,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                var remaining = maximumBytes - (int)buffer.Length;
                if (remaining > 0)
                {
                    buffer.Write(chunk, 0, remaining);
                }

                onOverflow();
                throw new OutputLimitExceededException();
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool TryKillTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(safety.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller receives the governed outcome; an OS-level kill failure remains visible
            // through ProcessTreeKilled=false rather than hanging orchestration indefinitely.
        }
    }

    private static async Task<(string Stdout, string Stderr)> DrainAfterTerminationAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        string stdout;
        string stderr;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
        }
        catch (OutputLimitExceededException)
        {
            stdout = string.Empty;
        }

        try
        {
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OutputLimitExceededException)
        {
            stderr = string.Empty;
        }

        return (stdout, stderr);
    }

    private sealed class OutputLimitExceededException : Exception
    {
    }
}
