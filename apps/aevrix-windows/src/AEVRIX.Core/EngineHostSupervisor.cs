using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Aevrix.Core;

/// <summary>
/// Owns one EngineHost process and the ephemeral authentication material used to reach it.
/// The supervisor fails closed: callers never receive a client until a real authenticated ping succeeds.
/// </summary>
public sealed class EngineHostSupervisor : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private EngineHostClient? _client;
    private string? _pipeName;
    private string? _token;
    private bool _disposed;

    public EngineHostSupervisor(
        string executablePath,
        IEnumerable<string>? arguments = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("EngineHost executable path is required.", nameof(executablePath));
        }

        _executablePath = executablePath;
        _arguments = arguments?.ToArray() ?? Array.Empty<string>();
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);

        if (_startupTimeout <= TimeSpan.Zero || _startupTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }

        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
    }

    public bool IsRunning => _process is { HasExited: false } && _client is not null;

    public int? ProcessId => IsRunning ? _process!.Id : null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);

            _pipeName = $"{EngineProtocol.PipeNamePrefix}{Guid.NewGuid():N}";
            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            var startInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in _arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment[EngineProtocol.PipeEnvironmentVariable] = _pipeName;
            startInfo.Environment[EngineProtocol.TokenEnvironmentVariable] = _token;
            startInfo.Environment[EngineProtocol.ParentProcessIdEnvironmentVariable] =
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start AEVRIX.EngineHost.");
            _client = new EngineHostClient(_pipeName, _token, _requestTimeout);

            try
            {
                await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EngineResponse> SendAsync(
        EngineCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();

        var client = _client;
        var process = _process;
        if (client is null || process is null || process.HasExited)
        {
            throw new InvalidOperationException("EngineHost is not running and authenticated.");
        }

        return await client.SendAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        if (_client is null || _process is null)
        {
            throw new InvalidOperationException("EngineHost startup state is incomplete.");
        }

        using var timeoutCts = new CancellationTokenSource(_startupTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        Exception? lastFailure = null;
        while (!linkedCts.IsCancellationRequested)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"EngineHost exited during startup with code {_process.ExitCode}.");
            }

            try
            {
                var response = await _client.SendAsync(
                    new EnginePingCommand(Guid.NewGuid().ToString("N")),
                    linkedCts.Token).ConfigureAwait(false);

                if (response.Success && string.Equals(response.Code, "pong", StringComparison.Ordinal))
                {
                    return;
                }

                lastFailure = new InvalidDataException(
                    $"EngineHost readiness probe failed closed with code '{response.Code}'.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
            {
                lastFailure = ex;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(75), linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("EngineHost did not become ready before the startup deadline.", lastFailure);
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        _client = null;
        _process = null;
        _pipeName = null;
        _token = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Process.Kill was already issued. Dispose the handle and remain fail-closed.
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
