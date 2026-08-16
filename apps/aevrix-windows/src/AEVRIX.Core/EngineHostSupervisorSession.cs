namespace Aevrix.Core;

/// <summary>
/// Adapts the concrete EngineHost process supervisor to the lifecycle boundary
/// consumed by product surfaces without exposing pipe credentials or process handles.
/// </summary>
public sealed class EngineHostSupervisorSession : IEngineHostSession
{
    private readonly EngineHostSupervisor _supervisor;

    public EngineHostSupervisorSession(EngineHostSupervisor supervisor)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
    }

    public bool IsRunning => _supervisor.IsRunning;

    public int? ProcessId => _supervisor.ProcessId;

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _supervisor.StartAsync(cancellationToken);

    public Task<EngineResponse> SendAsync(
        EngineCommand command,
        CancellationToken cancellationToken = default) =>
        _supervisor.SendAsync(command, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _supervisor.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => _supervisor.DisposeAsync();
}
