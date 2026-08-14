namespace Aevrix.Core;

public enum BrowserSessionState
{
    Created,
    AwaitingFirstLogin,
    Authenticated,
    Capturing,
    ReauthenticationRequired,
    CoolingDown,
    Paused,
    Closed
}

public sealed record LoginRecipe(
    string TargetId,
    Uri LoginUri,
    string UsernameSelector,
    string PasswordSelector,
    string SubmitSelector,
    IReadOnlyList<string> AuthenticatedUrlMarkers,
    IReadOnlyList<string> AuthenticatedTextMarkers,
    IReadOnlyList<string> LoggedOutUrlMarkers,
    IReadOnlyList<string> LoggedOutTextMarkers,
    DateTimeOffset LearnedAt)
{
    public LoginRecipe Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetId);
        ArgumentNullException.ThrowIfNull(LoginUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(UsernameSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(PasswordSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(SubmitSelector);

        if (LoginUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Stored login recipes require HTTPS targets.");
        }

        return this;
    }
}

public sealed record ResearchBrowserPolicy(
    string TargetId,
    IReadOnlyList<string> AllowedHosts,
    bool PersistTargetProfile,
    bool RememberCredentials,
    bool AutomaticRelogin,
    bool PauseImmediatelyOnLogout,
    int ShortWindowFailureThreshold,
    TimeSpan FailureWindow,
    TimeSpan Cooldown,
    bool ClearSiteDataWhenProjectDeleted,
    EgressPolicy EgressPolicy)
{
    public ResearchBrowserPolicy Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetId);
        if (AllowedHosts.Count == 0)
        {
            throw new InvalidOperationException("Research Browser requires an explicit target host allowlist.");
        }

        if (!PauseImmediatelyOnLogout)
        {
            throw new InvalidOperationException("Authenticated research must fail closed when the session is lost.");
        }

        if (ShortWindowFailureThreshold is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(ShortWindowFailureThreshold));
        }

        if (FailureWindow <= TimeSpan.Zero || Cooldown <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Session failure window and cooldown must be positive.");
        }

        EgressPolicy.Validate();
        return this;
    }

    public static ResearchBrowserPolicy SecureDefault(
        string targetId,
        IReadOnlyList<string> allowedHosts,
        EgressPolicy egressPolicy) => new ResearchBrowserPolicy(
            targetId,
            allowedHosts,
            PersistTargetProfile: true,
            RememberCredentials: true,
            AutomaticRelogin: true,
            PauseImmediatelyOnLogout: true,
            ShortWindowFailureThreshold: 3,
            FailureWindow: TimeSpan.FromMinutes(15),
            Cooldown: TimeSpan.FromMinutes(10),
            ClearSiteDataWhenProjectDeleted: false,
            EgressPolicy: egressPolicy).Validate();
}

public sealed record BrowserSessionObservation(
    Uri CurrentUri,
    bool VisiblePasswordField,
    IReadOnlyList<int> RecentFirstPartyStatuses,
    string VisibleTextSample,
    bool AuthenticatedSentinelSeen,
    DateTimeOffset ObservedAt);

public enum BrowserSessionDecisionKind
{
    Continue,
    PauseAndRelogin,
    OpenCircuitBreaker
}

public sealed record BrowserSessionDecision(
    BrowserSessionDecisionKind Kind,
    string Reason,
    DateTimeOffset? CooldownUntil = null);

public sealed class BrowserSessionGuard
{
    private readonly ResearchBrowserPolicy _policy;
    private readonly Queue<DateTimeOffset> _failures = new();

    public BrowserSessionGuard(ResearchBrowserPolicy policy)
    {
        _policy = policy.Validate();
    }

    public BrowserSessionDecision Evaluate(
        BrowserSessionObservation observation,
        LoginRecipe recipe,
        DateTimeOffset now)
    {
        recipe.Validate();
        Trim(now);

        var lostReason = DetectLoss(observation, recipe);
        if (lostReason is null)
        {
            return new BrowserSessionDecision(BrowserSessionDecisionKind.Continue, "Authenticated session appears healthy.");
        }

        _failures.Enqueue(now);
        Trim(now);

        if (_failures.Count >= _policy.ShortWindowFailureThreshold)
        {
            return new BrowserSessionDecision(
                BrowserSessionDecisionKind.OpenCircuitBreaker,
                $"{lostReason} Repeated session-loss threshold reached; network identity is not rotated automatically.",
                now + _policy.Cooldown);
        }

        return new BrowserSessionDecision(BrowserSessionDecisionKind.PauseAndRelogin, lostReason);
    }

    private string? DetectLoss(BrowserSessionObservation observation, LoginRecipe recipe)
    {
        if (observation.RecentFirstPartyStatuses.Any(status => status is 401 or 403))
        {
            return "First-party service returned 401/403.";
        }

        if (observation.VisiblePasswordField)
        {
            return "Password field became visible again.";
        }

        var url = observation.CurrentUri.AbsoluteUri;
        if (recipe.LoggedOutUrlMarkers.Any(marker => url.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return "Current URL matches a logged-out marker.";
        }

        if (recipe.LoggedOutTextMarkers.Any(marker => observation.VisibleTextSample.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return "Page content matches a logged-out/session-expired marker.";
        }

        if (!observation.AuthenticatedSentinelSeen
            && (recipe.AuthenticatedTextMarkers.Count > 0 || recipe.AuthenticatedUrlMarkers.Count > 0))
        {
            return "Expected authenticated sentinel is no longer present.";
        }

        return null;
    }

    private void Trim(DateTimeOffset now)
    {
        while (_failures.TryPeek(out var timestamp) && now - timestamp > _policy.FailureWindow)
        {
            _failures.Dequeue();
        }
    }
}
