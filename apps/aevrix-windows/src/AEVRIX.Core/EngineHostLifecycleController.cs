namespace Aevrix.Core;

public interface IEngineHostSession : IAsyncDisposable
{
    bool IsRunning { get; }
    int? ProcessId { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<EngineResponse> SendAsync(EngineCommand command, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public enum EngineHostLifecycleState
{
    Stopped,
    Starting,
    Ready,
    Faulted,
    Stopping
}

public sealed record EngineHostLifecycleSnapshot(
    EngineHostLifecycleState State,
    int? ProcessId,
    string Code,
    string Message,
    DateTimeOffset ObservedAtUtc)
{
    public bool IsReady => State == EngineHostLifecycleState.Ready;
}

/// <summary>
/// Owns the product-facing lifecycle state for EngineHost.
/// A process being present is never enough to report Ready: readiness requires
/// an authenticated session plus a successful GetEngineStatus exchange.
/// </summary>
public sealed class EngineHostLifecycleController : IAsyncDisposable
{
    private readonly IEngineHostSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EngineHostLifecycleSnapshot _snapshot = NewSnapshot(
        EngineHostLifecycleState.Stopped,
        null,
        "engine_stopped",
        "EngineHost is stopped.");
    private bool _disposed;

    public EngineHostLifecycleController(IEngineHostSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public EngineHostLifecycleSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public async Task<EngineHostLifecycleSnapshot> StartAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.IsReady && _session.IsRunning)
            {
                return Snapshot;
            }

            Publish(EngineHostLifecycleState.Starting, _session.ProcessId, "engine_starting", "EngineHost is starting.");

            try
            {
                await _session.StartAsync(cancellationToken).ConfigureAwait(false);
                return await ProbeReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await StopAfterFailureAsync().ConfigureAwait(false);
                Publish(EngineHostLifecycleState.Stopped, null, "engine_start_cancelled", "EngineHost start was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                await StopAfterFailureAsync().ConfigureAwait(false);
                Publish(EngineHostLifecycleState.Faulted, null, "engine_start_failed", SafeMessage(ex));
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EngineHostLifecycleSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_session.IsRunning)
            {
                var previous = Snapshot.State;
                var code = previous == EngineHostLifecycleState.Stopped
                    ? "engine_stopped"
                    : "engine_unavailable";
                var message = previous == EngineHostLifecycleState.Stopped
                    ? "EngineHost is stopped."
                    : "EngineHost is no longer running.";
                Publish(
                    previous == EngineHostLifecycleState.Stopped
                        ? EngineHostLifecycleState.Stopped
                        : EngineHostLifecycleState.Faulted,
                    null,
                    code,
                    message);
                return Snapshot;
            }

            try
            {
                return await ProbeReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Publish(EngineHostLifecycleState.Faulted, _session.ProcessId, "engine_status_failed", SafeMessage(ex));
                return Snapshot;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EngineHostLifecycleSnapshot> StopAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_session.IsRunning && Snapshot.State == EngineHostLifecycleState.Stopped)
            {
                return Snapshot;
            }

            Publish(EngineHostLifecycleState.Stopping, _session.ProcessId, "engine_stopping", "EngineHost is stopping.");
            await _session.StopAsync(cancellationToken).ConfigureAwait(false);
            Publish(EngineHostLifecycleState.Stopped, null, "engine_stopped", "EngineHost is stopped.");
            return Snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Publish(EngineHostLifecycleState.Faulted, _session.ProcessId, "engine_stop_failed", SafeMessage(ex));
            throw;
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
            try
            {
                await _session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Disposal remains fail-closed. The owned session is disposed below.
            }

            Publish(EngineHostLifecycleState.Stopped, null, "engine_stopped", "EngineHost is stopped.");
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<EngineHostLifecycleSnapshot> ProbeReadyAsync(CancellationToken cancellationToken)
    {
        if (!_session.IsRunning)
        {
            throw new InvalidOperationException("EngineHost session is not running after startup.");
        }

        var response = await _session.SendAsync(
            new GetEngineStatusCommand(Guid.NewGuid().ToString("N")),
            cancellationToken).ConfigureAwait(false);

        if (!response.Success || !string.Equals(response.Code, "engine_ready", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"EngineHost status failed closed with code '{response.Code}'.");
        }

        if (!_session.IsRunning || _session.ProcessId is null)
        {
            throw new InvalidOperationException("EngineHost status succeeded without a live owned process.");
        }

        Publish(
            EngineHostLifecycleState.Ready,
            _session.ProcessId,
            response.Code,
            response.Message);
        return Snapshot;
    }

    private async Task StopAfterFailureAsync()
    {
        try
        {
            await _session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original startup failure. Session disposal remains authoritative cleanup.
        }
    }

    private void Publish(
        EngineHostLifecycleState state,
        int? processId,
        string code,
        string message)
    {
        Volatile.Write(ref _snapshot, NewSnapshot(state, processId, code, message));
    }

    private static EngineHostLifecycleSnapshot NewSnapshot(
        EngineHostLifecycleState state,
        int? processId,
        string code,
        string message) =>
        new(state, processId, code, message, DateTimeOffset.UtcNow);

    private static string SafeMessage(Exception ex) =>
        ex is TimeoutException
            ? "EngineHost did not satisfy its lifecycle deadline."
            : "EngineHost lifecycle operation failed closed.";

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
