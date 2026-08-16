using Aevrix.Core;

namespace AEVRIX.Desktop;

internal sealed record DesktopEngineStatus(
    bool Verified,
    string State,
    string Detail,
    int? ProcessId)
{
    public static DesktopEngineStatus Ready(int processId) => new(
        true,
        "Verificado",
        $"Sessão local autenticada com o EngineHost. PID {processId}.",
        processId);

    public static DesktopEngineStatus Blocked(string detail) => new(
        false,
        "Indisponível",
        detail,
        null);
}

/// <summary>
/// Desktop-owned adapter over the canonical EngineHostSupervisor. It never infers readiness:
/// the UI receives Verified=true only after the side-by-side EngineHost starts, authenticates,
/// and answers GetEngineStatus with the canonical engine_ready code.
/// </summary>
internal sealed class DesktopEngineSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _engineHostPath;
    private EngineHostSupervisor? _supervisor;
    private bool _disposed;

    public DesktopEngineSession(string? engineHostPath = null)
    {
        _engineHostPath = string.IsNullOrWhiteSpace(engineHostPath)
            ? Path.Combine(AppContext.BaseDirectory, "AEVRIX.EngineHost.exe")
            : Path.GetFullPath(engineHostPath);
    }

    public async Task<DesktopEngineStatus> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (!File.Exists(_engineHostPath))
            {
                await StopFailClosedAsync().ConfigureAwait(false);
                return DesktopEngineStatus.Blocked(
                    "O EngineHost instalado lado a lado não foi encontrado. O runtime permanece bloqueado.");
            }

            _supervisor ??= new EngineHostSupervisor(
                _engineHostPath,
                startupTimeout: TimeSpan.FromSeconds(15),
                requestTimeout: TimeSpan.FromSeconds(5));

            try
            {
                await _supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
                var response = await _supervisor.SendAsync(
                    new GetEngineStatusCommand(Guid.NewGuid().ToString("N")),
                    cancellationToken).ConfigureAwait(false);

                if (!response.Success
                    || !string.Equals(response.Code, "engine_ready", StringComparison.Ordinal)
                    || !_supervisor.IsRunning
                    || _supervisor.ProcessId is not int processId)
                {
                    await StopFailClosedAsync().ConfigureAwait(false);
                    return DesktopEngineStatus.Blocked(
                        $"O EngineHost respondeu com estado não confiável ('{response.Code}'). O runtime foi bloqueado.");
                }

                return DesktopEngineStatus.Ready(processId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException
                or InvalidDataException
                or InvalidOperationException
                or TimeoutException
                or UnauthorizedAccessException)
            {
                await StopFailClosedAsync().ConfigureAwait(false);
                return DesktopEngineStatus.Blocked(
                    $"A verificação autenticada do EngineHost falhou ({ex.GetType().Name}). O runtime permanece bloqueado.");
            }
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

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_supervisor is not null)
            {
                await _supervisor.DisposeAsync().ConfigureAwait(false);
                _supervisor = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task StopFailClosedAsync()
    {
        if (_supervisor is null)
        {
            return;
        }

        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _supervisor = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
