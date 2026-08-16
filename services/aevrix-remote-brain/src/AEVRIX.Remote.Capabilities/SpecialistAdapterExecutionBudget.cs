using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Capabilities;

public sealed record SpecialistAdapterExecutionPolicy(
    TimeSpan AttemptTimeout,
    bool FailoverOnTimeout = true,
    SpecialistAdapterExecutionEnvelope? Envelope = null)
{
    public static SpecialistAdapterExecutionPolicy Default { get; } =
        new(TimeSpan.FromMinutes(2));

    public SpecialistAdapterExecutionPolicy Validate()
    {
        if (AttemptTimeout < TimeSpan.FromMilliseconds(10)
            || AttemptTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(AttemptTimeout),
                "Adapter attempt timeout must be between 10 ms and 30 minutes.");
        }

        Envelope?.Validate();
        return this;
    }
}

public enum SpecialistAdapterAttemptOutcome
{
    Succeeded,
    Failed,
    TimedOut,
    EvidenceBoundaryRejected,
    ExecutionEnvelopeRejected,
    OutputBudgetRejected,
    CallerCancelled
}

public sealed record SpecialistAdapterAttemptTelemetry(
    string ProviderId,
    string Capability,
    MissionSpecialistKind Specialist,
    SpecialistAdapterAttemptOutcome Outcome,
    double ElapsedMilliseconds,
    double? OutputConfidence,
    string? ErrorType,
    DateTimeOffset ObservedAt)
{
    public SpecialistAdapterAttemptTelemetry Validate()
    {
        McpServerDescriptor.ValidateId(ProviderId, nameof(ProviderId));
        McpServerDescriptor.ValidateId(Capability, nameof(Capability));

        if (!double.IsFinite(ElapsedMilliseconds) || ElapsedMilliseconds < 0)
        {
            throw new InvalidDataException("Adapter attempt elapsed time is invalid.");
        }

        if (OutputConfidence is { } confidence
            && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
        {
            throw new InvalidDataException("Adapter attempt output confidence is invalid.");
        }

        if (ErrorType is { Length: > 200 })
        {
            throw new InvalidDataException("Adapter attempt error type exceeds its limit.");
        }

        if (ObservedAt == default)
        {
            throw new InvalidDataException("Adapter attempt observation timestamp is missing.");
        }

        return this;
    }
}

public interface ISpecialistAdapterAttemptObserver
{
    ValueTask ObserveAsync(
        SpecialistAdapterAttemptTelemetry telemetry,
        CancellationToken cancellationToken = default);
}

internal sealed class NullSpecialistAdapterAttemptObserver
    : ISpecialistAdapterAttemptObserver
{
    public static NullSpecialistAdapterAttemptObserver Instance { get; } = new();

    private NullSpecialistAdapterAttemptObserver()
    {
    }

    public ValueTask ObserveAsync(
        SpecialistAdapterAttemptTelemetry telemetry,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
