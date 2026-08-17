namespace Aevrix.Remote.Capabilities;

public enum AiProviderInvocationPhase
{
    Reserved,
    InvocationStarted,
    Completed
}

public sealed record AiProviderInvocationSnapshot(
    Guid ProjectId,
    string RequestId,
    string ProviderId,
    AiProviderInvocationPhase Phase,
    AiProviderBillingMode BillingMode,
    DateTimeOffset ReservedAt,
    DateTimeOffset? InvocationStartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Coordinates the transition from a budget reservation into an actual provider invocation.
/// Once provider execution has started, the reservation cannot be released with a normal
/// cancellation because a remote metered provider may already have charged for the request.
/// Such ambiguous outcomes remain reserved until an authoritative receipt or an explicit
/// future reconciliation path resolves them.
/// </summary>
public sealed class AiProviderInvocationCoordinator
{
    private readonly object _sync = new();
    private readonly AiProviderBudgetManager _budgets;
    private readonly TimeProvider _time;
    private readonly Dictionary<(Guid ProjectId, string RequestId), InvocationState> _states = new();

    public AiProviderInvocationCoordinator(
        AiProviderBudgetManager budgets,
        TimeProvider? timeProvider = null)
    {
        _budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
        _time = timeProvider ?? TimeProvider.System;
    }

    public AiBudgetReservationDecision Reserve(AiProviderCallEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var decision = _budgets.Reserve(estimate);
        if (!decision.Allowed || decision.Reservation is null)
        {
            return decision;
        }

        lock (_sync)
        {
            var key = (decision.Reservation.ProjectId, decision.Reservation.RequestId);
            if (_states.TryGetValue(key, out var existing))
            {
                if (!ReservationMatches(existing.Reservation, decision.Reservation))
                {
                    throw new InvalidDataException("Provider invocation request identity conflicts with an existing lifecycle reservation.");
                }

                return decision;
            }

            _states.Add(key, new InvocationState(decision.Reservation));
            return decision;
        }
    }

    public AiProviderInvocationSnapshot MarkInvocationStarted(Guid projectId, string requestId)
    {
        lock (_sync)
        {
            var state = GetState(projectId, requestId);
            if (state.Phase == AiProviderInvocationPhase.Completed)
            {
                throw new InvalidOperationException("A completed provider request cannot be started again.");
            }

            if (state.Phase == AiProviderInvocationPhase.Reserved)
            {
                state.Phase = AiProviderInvocationPhase.InvocationStarted;
                state.InvocationStartedAt = _time.GetUtcNow();
            }

            return Snapshot(state);
        }
    }

    public AiProjectBudgetSnapshot Complete(AiProviderUsageReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        lock (_sync)
        {
            var state = GetState(receipt.ProjectId, receipt.RequestId);
            if (state.Phase == AiProviderInvocationPhase.Reserved
                && state.Reservation.BillingMode == AiProviderBillingMode.ExternalMetered)
            {
                throw new InvalidOperationException("Externally metered provider usage cannot complete before invocation start is recorded.");
            }

            var snapshot = _budgets.Complete(receipt);
            if (state.Phase != AiProviderInvocationPhase.Completed)
            {
                state.Phase = AiProviderInvocationPhase.Completed;
                state.CompletedAt = _time.GetUtcNow();
            }

            return snapshot;
        }
    }

    public AiProjectBudgetSnapshot CancelBeforeInvocation(Guid projectId, string requestId)
    {
        lock (_sync)
        {
            var state = GetState(projectId, requestId);
            if (state.Phase == AiProviderInvocationPhase.InvocationStarted)
            {
                throw new InvalidOperationException(
                    "Provider invocation already started; budget remains reserved until authoritative usage reconciliation prevents duplicate spend.");
            }

            if (state.Phase == AiProviderInvocationPhase.Completed)
            {
                throw new InvalidOperationException("Completed provider usage cannot be cancelled.");
            }

            var snapshot = _budgets.Cancel(projectId, requestId);
            _states.Remove((projectId, requestId));
            return snapshot;
        }
    }

    public AiProviderInvocationSnapshot GetSnapshot(Guid projectId, string requestId)
    {
        lock (_sync)
        {
            return Snapshot(GetState(projectId, requestId));
        }
    }

    private InvocationState GetState(Guid projectId, string requestId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("RequestId is required.", nameof(requestId));
        }

        if (!_states.TryGetValue((projectId, requestId), out var state))
        {
            throw new KeyNotFoundException("No provider invocation lifecycle reservation exists for this request.");
        }

        return state;
    }

    private static AiProviderInvocationSnapshot Snapshot(InvocationState state) =>
        new(
            state.Reservation.ProjectId,
            state.Reservation.RequestId,
            state.Reservation.ProviderId,
            state.Phase,
            state.Reservation.BillingMode,
            state.Reservation.ReservedAt,
            state.InvocationStartedAt,
            state.CompletedAt);

    private static bool ReservationMatches(AiProviderBudgetReservation left, AiProviderBudgetReservation right) =>
        left.ProjectId == right.ProjectId
        && string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal)
        && string.Equals(left.ProviderId, right.ProviderId, StringComparison.OrdinalIgnoreCase)
        && left.Location == right.Location
        && left.BillingMode == right.BillingMode
        && string.Equals(left.CurrencyCode, right.CurrencyCode, StringComparison.Ordinal)
        && left.AuthorizedCostMicros == right.AuthorizedCostMicros
        && left.AuthorizedInputTokens == right.AuthorizedInputTokens
        && left.AuthorizedOutputTokens == right.AuthorizedOutputTokens;

    private sealed class InvocationState(AiProviderBudgetReservation reservation)
    {
        public AiProviderBudgetReservation Reservation { get; } = reservation;
        public AiProviderInvocationPhase Phase { get; set; } = AiProviderInvocationPhase.Reserved;
        public DateTimeOffset? InvocationStartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }
}
