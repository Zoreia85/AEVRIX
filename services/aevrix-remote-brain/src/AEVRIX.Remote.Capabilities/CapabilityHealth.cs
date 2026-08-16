using System.Diagnostics;

namespace Aevrix.Remote.Capabilities;

public sealed record CapabilityHealthObservation(
    string ProviderId,
    CapabilityHealthState Health,
    double LatencyMilliseconds,
    DateTimeOffset ObservedAt,
    string? Detail = null)
{
    public CapabilityHealthObservation Validate()
    {
        McpServerDescriptor.ValidateId(ProviderId, nameof(ProviderId));

        if (!double.IsFinite(LatencyMilliseconds) || LatencyMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LatencyMilliseconds));
        }

        if (ObservedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(ObservedAt));
        }

        if (Detail is { Length: > 512 })
        {
            throw new ArgumentOutOfRangeException(nameof(Detail));
        }

        return this;
    }
}

public interface ICapabilityHealthProbe
{
    string ProviderId { get; }

    Task<CapabilityHealthObservation> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes bounded active health checks and feeds the CapabilityBroker.
/// A probe failure is converted to an Unavailable observation instead of being allowed
/// to crash routing or silently leave a stale provider eligible.
/// </summary>
public sealed class CapabilityHealthMonitor
{
    private readonly CapabilityBroker _broker;
    private readonly IReadOnlyDictionary<string, ICapabilityHealthProbe> _probes;
    private readonly TimeProvider _time;

    public CapabilityHealthMonitor(
        CapabilityBroker broker,
        IEnumerable<ICapabilityHealthProbe> probes,
        TimeProvider? timeProvider = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        ArgumentNullException.ThrowIfNull(probes);

        var probeArray = probes.ToArray();
        if (probeArray.Any(probe => probe is null))
        {
            throw new ArgumentException("Health probe collection cannot contain null entries.", nameof(probes));
        }

        if (probeArray.Any(probe => string.IsNullOrWhiteSpace(probe.ProviderId)))
        {
            throw new ArgumentException("Every health probe requires a stable provider id.", nameof(probes));
        }

        if (probeArray.Select(probe => probe.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != probeArray.Length)
        {
            throw new ArgumentException("Health probe provider ids must be unique.", nameof(probes));
        }

        _probes = probeArray.ToDictionary(probe => probe.ProviderId, StringComparer.OrdinalIgnoreCase);
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<CapabilityHealthObservation> ProbeProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (!_probes.TryGetValue(providerId, out var probe))
        {
            throw new KeyNotFoundException($"Unknown health probe '{providerId}'.");
        }

        var observation = await ExecuteProbeAsync(probe, cancellationToken);
        _broker.RecordHealthObservation(observation);
        return observation;
    }

    public async Task<IReadOnlyList<CapabilityHealthObservation>> ProbeAllAsync(
        int maximumConcurrency = 4,
        CancellationToken cancellationToken = default)
    {
        if (maximumConcurrency is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        using var gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        var tasks = _probes.Values
            .OrderBy(probe => probe.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(async probe =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var observation = await ExecuteProbeAsync(probe, cancellationToken);
                    _broker.RecordHealthObservation(observation);
                    return observation;
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    private async Task<CapabilityHealthObservation> ExecuteProbeAsync(
        ICapabilityHealthProbe probe,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var observation = (await probe.ProbeAsync(cancellationToken)).Validate();
            if (!string.Equals(observation.ProviderId, probe.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Health probe identity mismatch. Expected '{probe.ProviderId}', received '{observation.ProviderId}'.");
            }

            return observation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CapabilityHealthObservation(
                probe.ProviderId,
                CapabilityHealthState.Unavailable,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                _time.GetUtcNow(),
                $"probe-failed:{exception.GetType().Name}").Validate();
        }
    }
}
