using System.Diagnostics;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Verifies that an Ollama runtime is reachable and that the model expected by the
/// registered AEVRIX provider is actually present. Missing models fail closed.
/// </summary>
public sealed class OllamaCapabilityHealthProbe : ICapabilityHealthProbe
{
    private readonly OllamaModelProvider _provider;
    private readonly string _expectedModel;
    private readonly TimeProvider _time;

    public OllamaCapabilityHealthProbe(
        OllamaModelProvider provider,
        string expectedModel,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        if (string.IsNullOrWhiteSpace(expectedModel)
            || expectedModel.Length > 160
            || expectedModel.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':' or '/')))
        {
            throw new ArgumentException("Expected Ollama model name is invalid.", nameof(expectedModel));
        }

        _expectedModel = expectedModel;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string ProviderId => _provider.ProviderId;

    public async Task<CapabilityHealthObservation> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var models = await _provider.ListModelsAsync(cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var model = models.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, _expectedModel, StringComparison.OrdinalIgnoreCase));

            return new CapabilityHealthObservation(
                ProviderId,
                model is null ? CapabilityHealthState.Unavailable : CapabilityHealthState.Healthy,
                elapsed,
                _time.GetUtcNow(),
                model is null ? "configured-model-not-present" : "runtime-and-model-ready").Validate();
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
                $"ollama-probe-failed:{exception.GetType().Name}").Validate();
        }
    }
}
