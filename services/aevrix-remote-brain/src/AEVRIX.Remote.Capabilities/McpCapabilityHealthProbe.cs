using System.Diagnostics;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Performs a bounded, read-only tools/list operation against an already-approved MCP server.
/// Transport/protocol failures mark the provider unavailable. A reachable server that publishes
/// malformed or rejected tool definitions is degraded rather than silently treated as healthy.
/// </summary>
public sealed class McpCapabilityHealthProbe : ICapabilityHealthProbe
{
    private readonly McpStreamableHttpClient _client;
    private readonly TimeProvider _time;

    public McpCapabilityHealthProbe(
        McpStreamableHttpClient client,
        TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _time = timeProvider ?? TimeProvider.System;
    }

    public string ProviderId => _client.ServerId;

    public async Task<CapabilityHealthObservation> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var catalog = await _client.ListToolsAsync(cancellationToken: cancellationToken);
            var health = catalog.RejectedTools.Count == 0
                ? CapabilityHealthState.Healthy
                : CapabilityHealthState.Degraded;
            var detail = catalog.RejectedTools.Count == 0
                ? $"mcp-ready:tools={catalog.Tools.Count}"
                : $"mcp-schema-degraded:accepted={catalog.Tools.Count};rejected={catalog.RejectedTools.Count}";

            return new CapabilityHealthObservation(
                ProviderId,
                health,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                _time.GetUtcNow(),
                detail).Validate();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CapabilityHealthObservation(
                ProviderId,
                CapabilityHealthState.Unavailable,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                _time.GetUtcNow(),
                $"mcp-probe-failed:{exception.GetType().Name}").Validate();
        }
    }
}
