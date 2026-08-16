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

    public static DesktopEngineStatus Stopped() => new(
        false,
        "Parado",
        "Sessão local encerrada. Nenhum estado saudável é considerado ativo.",
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

    public bool IsRunning => !_disposed && _supervisor?.IsRunning == true;

    public async Task<DesktopEngineStatus> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesktopEngineStatus> RestartAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopFailClosedAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DesktopEngineStatus> StopAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            await StopFailClosedAsync().ConfigureAwait(false);
            return DesktopEngineStatus.Stopped();
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

    private async Task<DesktopEngineStatus> RefreshLockedAsync(
        CancellationToken cancellationToken)
    {
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
            var requestId = Guid.NewGuid().ToString("N");
            var response = await _supervisor.SendAsync(
                new GetEngineStatusCommand(requestId),
                cancellationToken).ConfigureAwait(false);

            if (!response.Success
                || !string.Equals(response.Code, "engine_ready", StringComparison.Ordinal)
                || !string.Equals(response.RequestId, requestId, StringComparison.Ordinal)
                || !_supervisor.IsRunning
                || _supervisor.ProcessId is not int processId)
            {
                var responseCode = string.IsNullOrWhiteSpace(response.Code)
                    ? "sem código"
                    : response.Code;
                await StopFailClosedAsync().ConfigureAwait(false);
                return DesktopEngineStatus.Blocked(
                    $"O EngineHost respondeu com estado não confiável ('{responseCode}'). O runtime foi bloqueado.");
            }

            return DesktopEngineStatus.Ready(processId);
        }
        catch (OperationCanceledException)
        {
            await StopFailClosedAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or InvalidDataException
            or InvalidOperationException
            or TimeoutException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            await StopFailClosedAsync().ConfigureAwait(false);
            return DesktopEngineStatus.Blocked(
                $"A verificação autenticada do EngineHost falhou ({ex.GetType().Name}). O runtime permanece bloqueado.");
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
