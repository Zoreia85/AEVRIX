using System.Diagnostics;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Capabilities;

public interface IMissionSpecialistProviderAdapter
{
    string ProviderId { get; }
    MissionSpecialistKind Kind { get; }
    Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed class AdaptiveMissionSpecialist : IMissionSpecialist
{
    private readonly CapabilityBroker _broker;
    private readonly string _capability;
    private readonly IReadOnlyDictionary<string, IMissionSpecialistProviderAdapter> _providers;
    private readonly int _maximumAttempts;
    private readonly TimeSpan _maximumObservationAge;
    private readonly TimeProvider _time;

    public AdaptiveMissionSpecialist(
        MissionSpecialistKind kind,
        string capability,
        CapabilityBroker broker,
        IEnumerable<IMissionSpecialistProviderAdapter> providers,
        int maximumAttempts = 3,
        TimeSpan? maximumObservationAge = null,
        TimeProvider? timeProvider = null)
    {
        McpServerDescriptor.ValidateId(capability, nameof(capability));
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(providers);

        if (maximumAttempts is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        _maximumObservationAge = maximumObservationAge ?? TimeSpan.FromMinutes(5);
        if (_maximumObservationAge <= TimeSpan.Zero || _maximumObservationAge > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumObservationAge));
        }

        var list = providers.ToArray();
        if (list.Length is < 1 or > 64 || list.Any(provider => provider is null))
        {
            throw new ArgumentException("Provider adapter set is invalid.", nameof(providers));
        }

        foreach (var provider in list)
        {
            McpServerDescriptor.ValidateId(provider.ProviderId, nameof(providers));
            if (provider.Kind != kind)
            {
                throw new ArgumentException("Provider adapter specialist kind mismatch.", nameof(providers));
            }
        }

        _providers = list.ToDictionary(
            provider => provider.ProviderId,
            StringComparer.OrdinalIgnoreCase);

        if (_providers.Count != list.Length)
        {
            throw new ArgumentException("Provider adapter ids must be unique.", nameof(providers));
        }

        Kind = kind;
        _capability = capability;
        _broker = broker;
        _maximumAttempts = maximumAttempts;
        _time = timeProvider ?? TimeProvider.System;
    }

    public MissionSpecialistKind Kind { get; }

    public async Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Task.Validate();

        if (context.Task.Specialist != Kind)
        {
            throw new InvalidDataException("Task specialist kind mismatch.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var ranked = _broker
            .Rank(_capability, _time.GetUtcNow(), _maximumObservationAge)
            .Where(rank => _providers.ContainsKey(rank.ProviderId))
            .Take(_maximumAttempts)
            .ToArray();

        if (ranked.Length == 0)
        {
            throw new InvalidOperationException(
                $"No approved healthy adapter is available for '{_capability}'.");
        }

        var failures = new List<Exception>();
        foreach (var rank in ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = _providers[rank.ProviderId];
            var started = Stopwatch.GetTimestamp();

            try
            {
                var output = (await provider
                    .ExecuteAsync(context, cancellationToken)
                    .ConfigureAwait(false))
                    .Validate();

                var unknownEvidence = output.EvidenceIds
                    .Except(context.Task.EvidenceIds, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (unknownEvidence.Length > 0)
                {
                    throw new InvalidDataException(
                        "Provider output exceeded the governed evidence boundary.");
                }

                Record(provider.ProviderId, succeeded: true, output.Confidence, started);
                return output;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Record(provider.ProviderId, succeeded: false, observedQuality: 0, started);
                failures.Add(exception);
            }
        }

        throw new AggregateException(
            $"All attempted adapters failed for '{_capability}'.",
            failures);
    }

    private void Record(
        string providerId,
        bool succeeded,
        double observedQuality,
        long started)
    {
        var snapshot = _broker.Get(providerId);
        var now = _time.GetUtcNow();
        var observedAt = now < snapshot.LastObservedAt
            ? snapshot.LastObservedAt
            : now;

        _broker.RecordOutcome(
            providerId,
            succeeded,
            observedQuality,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            observedAt);
    }
}
