using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Capabilities;

public sealed record AdaptiveModelCouncilPolicy(
    string Capability = "model-analysis",
    int MaximumAttempts = 3,
    TimeSpan? MaximumObservationAge = null)
{
    public TimeSpan EffectiveMaximumObservationAge => MaximumObservationAge ?? TimeSpan.FromMinutes(10);

    public AdaptiveModelCouncilPolicy Validate()
    {
        McpServerDescriptor.ValidateId(Capability, nameof(Capability));

        if (MaximumAttempts is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
        }

        if (EffectiveMaximumObservationAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumObservationAge));
        }

        return this;
    }
}

/// <summary>
/// Routes model-analysis work across approved providers using CapabilityBroker ranking and deterministic failover.
/// This component is a replaceable council member, not trusted memory and not the final Judge.
/// </summary>
public sealed class AdaptiveModelCouncilProvider : IAevrixModelProvider
{
    private readonly CapabilityBroker _broker;
    private readonly IReadOnlyDictionary<string, IAevrixModelProvider> _providers;
    private readonly AdaptiveModelCouncilPolicy _policy;
    private readonly TimeProvider _time;

    public AdaptiveModelCouncilProvider(
        CapabilityBroker broker,
        IEnumerable<IAevrixModelProvider> providers,
        AdaptiveModelCouncilPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        ArgumentNullException.ThrowIfNull(providers);

        var providerArray = providers.ToArray();
        if (providerArray.Length == 0)
        {
            throw new ArgumentException("At least one model provider is required.", nameof(providers));
        }

        if (providerArray.Any(provider => provider is null))
        {
            throw new ArgumentException("Model provider collection cannot contain null entries.", nameof(providers));
        }

        if (providerArray.Any(provider => string.IsNullOrWhiteSpace(provider.ProviderId)))
        {
            throw new ArgumentException("Every model provider requires a stable provider id.", nameof(providers));
        }

        if (providerArray.Select(provider => provider.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != providerArray.Length)
        {
            throw new ArgumentException("Model provider ids must be unique.", nameof(providers));
        }

        _providers = providerArray.ToDictionary(provider => provider.ProviderId, StringComparer.OrdinalIgnoreCase);
        _policy = (policy ?? new AdaptiveModelCouncilPolicy()).Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    public string ProviderId => "aevrix-adaptive-model-council";

    public async Task<ModelAnalysisCandidate> AnalyzeAsync(
        AnalysisTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.Validate();

        var now = _time.GetUtcNow();
        var ranked = _broker.Rank(_policy.Capability, now, _policy.EffectiveMaximumObservationAge);
        if (ranked.Count == 0)
        {
            throw new InvalidOperationException($"No approved healthy provider is available for capability '{_policy.Capability}'.");
        }

        var failures = new List<Exception>();
        var attempted = 0;

        foreach (var rank in ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempted >= _policy.MaximumAttempts)
            {
                break;
            }

            if (!_providers.TryGetValue(rank.ProviderId, out var provider))
            {
                failures.Add(new InvalidOperationException(
                    $"Capability provider '{rank.ProviderId}' is ranked but has no registered model implementation."));
                continue;
            }

            attempted++;
            var startedAt = _time.GetUtcNow();

            try
            {
                var candidate = (await provider.AnalyzeAsync(task, cancellationToken)).Validate();
                EnsureProviderIdentity(provider, candidate);

                var completedAt = _time.GetUtcNow();
                _broker.RecordOutcome(
                    provider.ProviderId,
                    succeeded: true,
                    observedQuality: candidate.Confidence,
                    latencyMilliseconds: ElapsedMilliseconds(startedAt, completedAt),
                    observedAt: completedAt);

                return candidate;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failedAt = _time.GetUtcNow();
                _broker.RecordOutcome(
                    provider.ProviderId,
                    succeeded: false,
                    observedQuality: 0,
                    latencyMilliseconds: ElapsedMilliseconds(startedAt, failedAt),
                    observedAt: failedAt);

                failures.Add(new InvalidOperationException(
                    $"Model provider '{provider.ProviderId}' failed during adaptive council execution.", ex));
            }
        }

        if (failures.Count == 0)
        {
            throw new InvalidOperationException("No ranked capability provider had a registered model implementation within the attempt budget.");
        }

        throw new AggregateException("All attempted adaptive model council providers failed.", failures);
    }

    private static void EnsureProviderIdentity(IAevrixModelProvider provider, ModelAnalysisCandidate candidate)
    {
        if (!string.Equals(candidate.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Provider identity mismatch. Expected '{provider.ProviderId}', received '{candidate.ProviderId}'.");
        }
    }

    private static double ElapsedMilliseconds(DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        if (completedAt <= startedAt)
        {
            return 0;
        }

        return (completedAt - startedAt).TotalMilliseconds;
    }
}
