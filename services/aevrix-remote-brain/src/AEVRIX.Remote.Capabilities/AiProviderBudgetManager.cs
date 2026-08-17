namespace Aevrix.Remote.Capabilities;

public enum AiProviderBudgetProfile
{
    LocalOnly,
    Economy,
    Balanced,
    MaximumQuality,
    Custom
}

public enum AiProviderLocation
{
    Local,
    Remote
}

public enum AiProviderBillingMode
{
    None,
    ExternalMetered
}

public enum AiBudgetDenialReason
{
    None,
    RemoteProviderDenied,
    ProviderNotAllowed,
    MeteredCallLimitExceeded,
    SpendLimitExceeded,
    InputTokenLimitExceeded,
    OutputTokenLimitExceeded,
    EstimatedLatencyLimitExceeded,
    RequestAlreadyCompleted,
    RequestIdConflict
}

public sealed record AiProjectBudgetPolicy(
    Guid ProjectId,
    AiProviderBudgetProfile Profile,
    string CurrencyCode,
    long MaximumSpendMicros,
    int MaximumMeteredCalls,
    long MaximumInputTokens,
    long MaximumOutputTokens,
    int? MaximumEstimatedLatencyMilliseconds = null,
    IReadOnlyList<string>? AllowedProviderIds = null)
{
    public AiProjectBudgetPolicy Validate()
    {
        if (ProjectId == Guid.Empty)
        {
            throw new ArgumentException("ProjectId is required.", nameof(ProjectId));
        }

        if (string.IsNullOrWhiteSpace(CurrencyCode)
            || CurrencyCode.Length != 3
            || CurrencyCode.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("CurrencyCode must be a three-letter uppercase ISO-style code.", nameof(CurrencyCode));
        }

        if (MaximumSpendMicros < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSpendMicros));
        }

        if (MaximumMeteredCalls < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMeteredCalls));
        }

        if (MaximumInputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumInputTokens));
        }

        if (MaximumOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputTokens));
        }

        if (MaximumEstimatedLatencyMilliseconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEstimatedLatencyMilliseconds));
        }

        if (AllowedProviderIds is not null)
        {
            if (AllowedProviderIds.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Allowed provider ids cannot contain blank entries.", nameof(AllowedProviderIds));
            }

            if (AllowedProviderIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != AllowedProviderIds.Count)
            {
                throw new ArgumentException("Allowed provider ids must be unique.", nameof(AllowedProviderIds));
            }
        }

        return this;
    }
}

public sealed record AiProviderCallEstimate(
    Guid ProjectId,
    string RequestId,
    string ProviderId,
    AiProviderLocation Location,
    AiProviderBillingMode BillingMode,
    string CurrencyCode,
    long EstimatedCostMicros,
    long EstimatedInputTokens,
    long EstimatedOutputTokens,
    int? EstimatedLatencyMilliseconds = null)
{
    public AiProviderCallEstimate Validate()
    {
        if (ProjectId == Guid.Empty)
        {
            throw new ArgumentException("ProjectId is required.", nameof(ProjectId));
        }

        ValidateStableId(RequestId, nameof(RequestId));
        ValidateStableId(ProviderId, nameof(ProviderId));

        if (string.IsNullOrWhiteSpace(CurrencyCode)
            || CurrencyCode.Length != 3
            || CurrencyCode.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("CurrencyCode must be a three-letter uppercase ISO-style code.", nameof(CurrencyCode));
        }

        if (EstimatedCostMicros < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(EstimatedCostMicros));
        }

        if (EstimatedInputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(EstimatedInputTokens));
        }

        if (EstimatedOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(EstimatedOutputTokens));
        }

        if (EstimatedLatencyMilliseconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(EstimatedLatencyMilliseconds));
        }

        if (BillingMode == AiProviderBillingMode.None && EstimatedCostMicros != 0)
        {
            throw new ArgumentException("Unmetered calls cannot reserve external spend.", nameof(EstimatedCostMicros));
        }

        if (Location == AiProviderLocation.Local && BillingMode == AiProviderBillingMode.ExternalMetered)
        {
            throw new ArgumentException("Local execution cannot be classified as externally metered.", nameof(BillingMode));
        }

        return this;
    }

    private static void ValidateStableId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException("A non-blank stable id up to 128 characters is required.", parameterName);
        }
    }
}

public sealed record AiProviderBudgetReservation(
    Guid ProjectId,
    string RequestId,
    string ProviderId,
    AiProviderLocation Location,
    AiProviderBillingMode BillingMode,
    string CurrencyCode,
    long AuthorizedCostMicros,
    long AuthorizedInputTokens,
    long AuthorizedOutputTokens,
    DateTimeOffset ReservedAt);

public sealed record AiProviderUsageReceipt(
    Guid ProjectId,
    string RequestId,
    string ProviderId,
    string CurrencyCode,
    long ActualCostMicros,
    long ActualInputTokens,
    long ActualOutputTokens)
{
    public AiProviderUsageReceipt Validate()
    {
        if (ProjectId == Guid.Empty)
        {
            throw new ArgumentException("ProjectId is required.", nameof(ProjectId));
        }

        if (string.IsNullOrWhiteSpace(RequestId) || string.IsNullOrWhiteSpace(ProviderId))
        {
            throw new ArgumentException("RequestId and ProviderId are required.");
        }

        if (string.IsNullOrWhiteSpace(CurrencyCode)
            || CurrencyCode.Length != 3
            || CurrencyCode.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("CurrencyCode must be a three-letter uppercase ISO-style code.", nameof(CurrencyCode));
        }

        if (ActualCostMicros < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ActualCostMicros));
        }

        if (ActualInputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ActualInputTokens));
        }

        if (ActualOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ActualOutputTokens));
        }

        return this;
    }
}

public sealed record AiProjectBudgetSnapshot(
    Guid ProjectId,
    long CommittedSpendMicros,
    long ReservedSpendMicros,
    int CommittedMeteredCalls,
    int ReservedMeteredCalls,
    long CommittedInputTokens,
    long ReservedInputTokens,
    long CommittedOutputTokens,
    long ReservedOutputTokens,
    long RemainingSpendMicros,
    int RemainingMeteredCalls,
    long RemainingInputTokens,
    long RemainingOutputTokens);

public sealed record AiBudgetReservationDecision(
    bool Allowed,
    AiBudgetDenialReason DenialReason,
    AiProviderBudgetReservation? Reservation,
    AiProjectBudgetSnapshot Snapshot)
{
    public static AiBudgetReservationDecision Permit(
        AiProviderBudgetReservation reservation,
        AiProjectBudgetSnapshot snapshot) =>
        new(true, AiBudgetDenialReason.None, reservation, snapshot);

    public static AiBudgetReservationDecision Deny(
        AiBudgetDenialReason reason,
        AiProjectBudgetSnapshot snapshot) =>
        new(false, reason, null, snapshot);
}

/// <summary>
/// Project-bound admission and accounting gate for local/remote AI provider calls.
/// It stores non-secret usage metadata only. API keys, access tokens and provider credentials
/// are deliberately outside this contract and belong to the platform secret boundary.
/// </summary>
public sealed class AiProviderBudgetManager
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ProjectBudgetState> _projects;
    private readonly TimeProvider _time;

    public AiProviderBudgetManager(
        IEnumerable<AiProjectBudgetPolicy> policies,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var validated = policies
            .Select(policy => (policy ?? throw new ArgumentException("Policies cannot contain null entries.", nameof(policies))).Validate())
            .ToArray();

        if (validated.Length == 0)
        {
            throw new ArgumentException("At least one project budget policy is required.", nameof(policies));
        }

        if (validated.Select(policy => policy.ProjectId).Distinct().Count() != validated.Length)
        {
            throw new ArgumentException("Project budget policies must have unique ProjectId values.", nameof(policies));
        }

        _projects = validated.ToDictionary(policy => policy.ProjectId, policy => new ProjectBudgetState(policy));
        _time = timeProvider ?? TimeProvider.System;
    }

    public AiBudgetReservationDecision Reserve(AiProviderCallEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        estimate.Validate();

        lock (_sync)
        {
            var state = GetState(estimate.ProjectId);
            var policy = state.Policy;
            EnsureCurrency(policy, estimate.CurrencyCode);

            if (state.Completed.ContainsKey(estimate.RequestId))
            {
                return AiBudgetReservationDecision.Deny(AiBudgetDenialReason.RequestAlreadyCompleted, Snapshot(state));
            }

            if (state.Reservations.TryGetValue(estimate.RequestId, out var existing))
            {
                return ReservationMatchesEstimate(existing, estimate)
                    ? AiBudgetReservationDecision.Permit(existing, Snapshot(state))
                    : AiBudgetReservationDecision.Deny(AiBudgetDenialReason.RequestIdConflict, Snapshot(state));
            }

            if (policy.Profile == AiProviderBudgetProfile.LocalOnly && estimate.Location != AiProviderLocation.Local)
            {
                return AiBudgetReservationDecision.Deny(AiBudgetDenialReason.RemoteProviderDenied, Snapshot(state));
            }

            if (policy.AllowedProviderIds is not null
                && !policy.AllowedProviderIds.Contains(estimate.ProviderId, StringComparer.OrdinalIgnoreCase))
            {
                return AiBudgetReservationDecision.Deny(AiBudgetDenialReason.ProviderNotAllowed, Snapshot(state));
            }

            var snapshot = Snapshot(state);
            var meteredCall = estimate.BillingMode == AiProviderBillingMode.ExternalMetered ? 1 : 0;
            if (meteredCall > snapshot.RemainingMeteredCalls)
            {
                return AiBudgetReservationDecision.Deny(AiBudgetDenialReason.MeteredCallLimitExceeded, snapshot);
            }

            if (estimate.EstimatedCostMicros > snapshot.RemainingSpendMicros)
            {
                return AiBudgetReservationDecision.Deny(AiBudgetDenialReason.SpendLimitExceeded, snapshot);
            }

            if (estimate.EstimatedInputTokens > snapshot.RemainingInputTokens)
            {
                return AiBudgetReservationDecision.Deny(AiBudgetDenialReason.InputTokenLimitExceeded, snapshot);
            }

            if (estimate.EstimatedOutputTokens > snapshot.RemainingOutputTokens)
            {
                return AiBudgetReservationDecision.Deny(AiBudgetDenialReason.OutputTokenLimitExceeded, snapshot);
            }

            if (policy.MaximumEstimatedLatencyMilliseconds is int latencyLimit
                && estimate.EstimatedLatencyMilliseconds is int estimatedLatency
                && estimatedLatency > latencyLimit)
            {
                return AiBudgetReservationDecision.Deny(AiBudgetDenialReason.EstimatedLatencyLimitExceeded, snapshot);
            }

            var reservation = new AiProviderBudgetReservation(
                estimate.ProjectId,
                estimate.RequestId,
                estimate.ProviderId,
                estimate.Location,
                estimate.BillingMode,
                policy.CurrencyCode,
                estimate.EstimatedCostMicros,
                estimate.EstimatedInputTokens,
                estimate.EstimatedOutputTokens,
                _time.GetUtcNow());

            state.Reservations.Add(reservation.RequestId, reservation);
            return AiBudgetReservationDecision.Permit(reservation, Snapshot(state));
        }
    }

    public AiProjectBudgetSnapshot Complete(AiProviderUsageReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();

        lock (_sync)
        {
            var state = GetState(receipt.ProjectId);
            EnsureCurrency(state.Policy, receipt.CurrencyCode);

            if (state.Completed.TryGetValue(receipt.RequestId, out var completed))
            {
                if (completed == receipt)
                {
                    return Snapshot(state);
                }

                throw new InvalidDataException("A completed AI provider request cannot be rebound to a different usage receipt.");
            }

            if (!state.Reservations.TryGetValue(receipt.RequestId, out var reservation))
            {
                throw new InvalidOperationException("No active budget reservation exists for this provider request.");
            }

            if (!string.Equals(reservation.ProviderId, receipt.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Provider usage receipt identity does not match the authorized reservation.");
            }

            if (receipt.ActualCostMicros > reservation.AuthorizedCostMicros
                || receipt.ActualInputTokens > reservation.AuthorizedInputTokens
                || receipt.ActualOutputTokens > reservation.AuthorizedOutputTokens)
            {
                throw new InvalidDataException("Provider usage exceeded the authorized reservation and requires explicit reconciliation.");
            }

            state.Reservations.Remove(receipt.RequestId);
            state.Completed.Add(receipt.RequestId, receipt);
            state.CommittedSpendMicros = checked(state.CommittedSpendMicros + receipt.ActualCostMicros);
            state.CommittedInputTokens = checked(state.CommittedInputTokens + receipt.ActualInputTokens);
            state.CommittedOutputTokens = checked(state.CommittedOutputTokens + receipt.ActualOutputTokens);
            if (reservation.BillingMode == AiProviderBillingMode.ExternalMetered)
            {
                state.CommittedMeteredCalls = checked(state.CommittedMeteredCalls + 1);
            }

            return Snapshot(state);
        }
    }

    public AiProjectBudgetSnapshot Cancel(Guid projectId, string requestId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("RequestId is required.", nameof(requestId));
        }

        lock (_sync)
        {
            var state = GetState(projectId);
            if (state.Completed.ContainsKey(requestId))
            {
                throw new InvalidOperationException("Completed provider usage cannot be cancelled from the budget ledger.");
            }

            state.Reservations.Remove(requestId);
            return Snapshot(state);
        }
    }

    public AiProjectBudgetSnapshot GetSnapshot(Guid projectId)
    {
        lock (_sync)
        {
            return Snapshot(GetState(projectId));
        }
    }

    private ProjectBudgetState GetState(Guid projectId)
    {
        if (!_projects.TryGetValue(projectId, out var state))
        {
            throw new KeyNotFoundException("No AI provider budget policy is registered for this project.");
        }

        return state;
    }

    private static void EnsureCurrency(AiProjectBudgetPolicy policy, string currencyCode)
    {
        if (!string.Equals(policy.CurrencyCode, currencyCode, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Provider usage currency does not match the project budget currency.");
        }
    }

    private static bool ReservationMatchesEstimate(AiProviderBudgetReservation reservation, AiProviderCallEstimate estimate) =>
        reservation.ProjectId == estimate.ProjectId
        && string.Equals(reservation.RequestId, estimate.RequestId, StringComparison.Ordinal)
        && string.Equals(reservation.ProviderId, estimate.ProviderId, StringComparison.OrdinalIgnoreCase)
        && reservation.Location == estimate.Location
        && reservation.BillingMode == estimate.BillingMode
        && string.Equals(reservation.CurrencyCode, estimate.CurrencyCode, StringComparison.Ordinal)
        && reservation.AuthorizedCostMicros == estimate.EstimatedCostMicros
        && reservation.AuthorizedInputTokens == estimate.EstimatedInputTokens
        && reservation.AuthorizedOutputTokens == estimate.EstimatedOutputTokens;

    private static AiProjectBudgetSnapshot Snapshot(ProjectBudgetState state)
    {
        var reservedSpend = state.Reservations.Values.Sum(item => item.AuthorizedCostMicros);
        var reservedMeteredCalls = state.Reservations.Values.Count(item => item.BillingMode == AiProviderBillingMode.ExternalMetered);
        var reservedInput = state.Reservations.Values.Sum(item => item.AuthorizedInputTokens);
        var reservedOutput = state.Reservations.Values.Sum(item => item.AuthorizedOutputTokens);

        return new AiProjectBudgetSnapshot(
            state.Policy.ProjectId,
            state.CommittedSpendMicros,
            reservedSpend,
            state.CommittedMeteredCalls,
            reservedMeteredCalls,
            state.CommittedInputTokens,
            reservedInput,
            state.CommittedOutputTokens,
            reservedOutput,
            Math.Max(0, state.Policy.MaximumSpendMicros - state.CommittedSpendMicros - reservedSpend),
            Math.Max(0, state.Policy.MaximumMeteredCalls - state.CommittedMeteredCalls - reservedMeteredCalls),
            Math.Max(0, state.Policy.MaximumInputTokens - state.CommittedInputTokens - reservedInput),
            Math.Max(0, state.Policy.MaximumOutputTokens - state.CommittedOutputTokens - reservedOutput));
    }

    private sealed class ProjectBudgetState(AiProjectBudgetPolicy policy)
    {
        public AiProjectBudgetPolicy Policy { get; } = policy;
        public Dictionary<string, AiProviderBudgetReservation> Reservations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AiProviderUsageReceipt> Completed { get; } = new(StringComparer.Ordinal);
        public long CommittedSpendMicros { get; set; }
        public int CommittedMeteredCalls { get; set; }
        public long CommittedInputTokens { get; set; }
        public long CommittedOutputTokens { get; set; }
    }
}
