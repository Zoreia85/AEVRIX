using System.Collections.Concurrent;

namespace Aevrix.Remote.Capabilities;

public enum CapabilityHealthState
{
    Healthy,
    Degraded,
    Unavailable,
    Quarantined
}

public sealed record CapabilityProviderSnapshot(
    string ProviderId,
    string Capability,
    CapabilityApprovalState Approval,
    CapabilityHealthState Health,
    bool Enabled,
    double QualityScore,
    double ReliabilityScore,
    double P95LatencyMilliseconds,
    int ConsecutiveFailures,
    DateTimeOffset LastObservedAt)
{
    public CapabilityProviderSnapshot Validate()
    {
        McpServerDescriptor.ValidateId(ProviderId, nameof(ProviderId));
        McpServerDescriptor.ValidateId(Capability, nameof(Capability));

        if (!double.IsFinite(QualityScore) || QualityScore is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(QualityScore));
        }

        if (!double.IsFinite(ReliabilityScore) || ReliabilityScore is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ReliabilityScore));
        }

        if (!double.IsFinite(P95LatencyMilliseconds) || P95LatencyMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(P95LatencyMilliseconds));
        }

        if (ConsecutiveFailures < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ConsecutiveFailures));
        }

        if (LastObservedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(LastObservedAt));
        }

        return this;
    }
}

public sealed record CapabilityProviderRank(
    string ProviderId,
    string Capability,
    double Score,
    CapabilityHealthState Health,
    double QualityScore,
    double ReliabilityScore,
    double P95LatencyMilliseconds,
    int ConsecutiveFailures);

public sealed class CapabilityBroker
{
    private const double QualityWeight = 0.50;
    private const double ReliabilityWeight = 0.35;
    private const double LatencyWeight = 0.10;
    private const double HealthWeight = 0.05;
    private const double OutcomeAlpha = 0.25;

    private readonly ConcurrentDictionary<string, CapabilityProviderSnapshot> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(CapabilityProviderSnapshot provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.Validate();
        _providers[provider.ProviderId] = provider;
    }

    public CapabilityProviderSnapshot Get(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new KeyNotFoundException($"Unknown capability provider '{providerId}'.");
    }

    public IReadOnlyList<CapabilityProviderRank> Rank(
        string capability,
        DateTimeOffset now,
        TimeSpan maximumObservationAge)
    {
        McpServerDescriptor.ValidateId(capability, nameof(capability));

        if (now == default)
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        if (maximumObservationAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumObservationAge));
        }

        return _providers.Values
            .Where(provider => string.Equals(provider.Capability, capability, StringComparison.OrdinalIgnoreCase))
            .Where(provider => provider.Enabled)
            .Where(provider => provider.Approval == CapabilityApprovalState.Approved)
            .Where(provider => provider.Health is CapabilityHealthState.Healthy or CapabilityHealthState.Degraded)
            .Where(provider => now >= provider.LastObservedAt && now - provider.LastObservedAt <= maximumObservationAge)
            .Select(provider => new CapabilityProviderRank(
                provider.ProviderId,
                provider.Capability,
                ComputeScore(provider),
                provider.Health,
                provider.QualityScore,
                provider.ReliabilityScore,
                provider.P95LatencyMilliseconds,
                provider.ConsecutiveFailures))
            .OrderByDescending(rank => rank.Score)
            .ThenBy(rank => rank.P95LatencyMilliseconds)
            .ThenBy(rank => rank.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public CapabilityProviderRank SelectBest(
        string capability,
        DateTimeOffset now,
        TimeSpan maximumObservationAge)
    {
        var ranked = Rank(capability, now, maximumObservationAge);
        return ranked.Count > 0
            ? ranked[0]
            : throw new InvalidOperationException($"No approved healthy provider is available for capability '{capability}'.");
    }

    public CapabilityProviderSnapshot RecordOutcome(
        string providerId,
        bool succeeded,
        double observedQuality,
        double latencyMilliseconds,
        DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (!double.IsFinite(observedQuality) || observedQuality is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(observedQuality));
        }

        if (!double.IsFinite(latencyMilliseconds) || latencyMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(latencyMilliseconds));
        }

        if (observedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(observedAt));
        }

        while (true)
        {
            if (!_providers.TryGetValue(providerId, out var current))
            {
                throw new KeyNotFoundException($"Unknown capability provider '{providerId}'.");
            }

            if (observedAt < current.LastObservedAt)
            {
                throw new InvalidOperationException("Capability outcomes must be recorded in non-decreasing observation order.");
            }

            var failures = succeeded ? 0 : checked(current.ConsecutiveFailures + 1);
            var health = current.Health == CapabilityHealthState.Quarantined
                ? CapabilityHealthState.Quarantined
                : succeeded
                    ? CapabilityHealthState.Healthy
                    : failures >= 3
                        ? CapabilityHealthState.Unavailable
                        : CapabilityHealthState.Degraded;

            var qualitySample = succeeded ? observedQuality : 0;
            var reliabilitySample = succeeded ? 1d : 0d;
            var updated = current with
            {
                Health = health,
                QualityScore = Ewma(current.QualityScore, qualitySample),
                ReliabilityScore = Ewma(current.ReliabilityScore, reliabilitySample),
                P95LatencyMilliseconds = Math.Max(
                    latencyMilliseconds,
                    Ewma(current.P95LatencyMilliseconds, latencyMilliseconds)),
                ConsecutiveFailures = failures,
                LastObservedAt = observedAt
            };

            updated.Validate();
            if (_providers.TryUpdate(providerId, updated, current))
            {
                return updated;
            }
        }
    }

    public CapabilityProviderSnapshot RecordHealthObservation(CapabilityHealthObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.Validate();

        while (true)
        {
            if (!_providers.TryGetValue(observation.ProviderId, out var current))
            {
                throw new KeyNotFoundException($"Unknown capability provider '{observation.ProviderId}'.");
            }

            if (observation.ObservedAt < current.LastObservedAt)
            {
                throw new InvalidOperationException("Capability health observations must be recorded in non-decreasing order.");
            }

            var requestedHealth = observation.Health;
            var health = current.Health == CapabilityHealthState.Quarantined
                ? CapabilityHealthState.Quarantined
                : requestedHealth;
            var failures = requestedHealth == CapabilityHealthState.Healthy
                ? 0
                : requestedHealth == CapabilityHealthState.Quarantined
                    ? current.ConsecutiveFailures
                    : checked(current.ConsecutiveFailures + 1);
            var reliabilitySample = requestedHealth switch
            {
                CapabilityHealthState.Healthy => 1d,
                CapabilityHealthState.Degraded => 0.5d,
                _ => 0d
            };

            var updated = current with
            {
                Health = health,
                ReliabilityScore = Ewma(current.ReliabilityScore, reliabilitySample),
                P95LatencyMilliseconds = Math.Max(
                    observation.LatencyMilliseconds,
                    Ewma(current.P95LatencyMilliseconds, observation.LatencyMilliseconds)),
                ConsecutiveFailures = failures,
                LastObservedAt = observation.ObservedAt
            };

            updated.Validate();
            if (_providers.TryUpdate(observation.ProviderId, updated, current))
            {
                return updated;
            }
        }
    }

    public CapabilityProviderSnapshot SetQuarantined(string providerId, bool quarantined)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        while (true)
        {
            if (!_providers.TryGetValue(providerId, out var current))
            {
                throw new KeyNotFoundException($"Unknown capability provider '{providerId}'.");
            }

            var updated = current with
            {
                Health = quarantined
                    ? CapabilityHealthState.Quarantined
                    : CapabilityHealthState.Degraded
            };

            if (_providers.TryUpdate(providerId, updated, current))
            {
                return updated;
            }
        }
    }

    private static double ComputeScore(CapabilityProviderSnapshot provider)
    {
        var latencyScore = 1d / (1d + (provider.P95LatencyMilliseconds / 1_000d));
        var healthScore = provider.Health == CapabilityHealthState.Healthy ? 1d : 0.45d;
        var failurePenalty = Math.Min(0.30d, provider.ConsecutiveFailures * 0.10d);

        return Math.Clamp(
            (provider.QualityScore * QualityWeight)
            + (provider.ReliabilityScore * ReliabilityWeight)
            + (latencyScore * LatencyWeight)
            + (healthScore * HealthWeight)
            - failurePenalty,
            0,
            1);
    }

    private static double Ewma(double previous, double sample) =>
        (previous * (1d - OutcomeAlpha)) + (sample * OutcomeAlpha);
}
