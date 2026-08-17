namespace Aevrix.Core;

public enum BrowserLoginOutcomeKind
{
    Authenticated,
    StillLoggedOut,
    Rejected,
    Indeterminate
}

public sealed record BrowserLoginOutcome(
    BrowserLoginOutcomeKind Kind,
    string Code,
    IReadOnlyList<string> Evidence)
{
    public static BrowserLoginOutcome Authenticated(IEnumerable<string> evidence) =>
        new(BrowserLoginOutcomeKind.Authenticated, "login_authenticated", evidence.ToArray());

    public static BrowserLoginOutcome LoggedOut(string code, IEnumerable<string> evidence) =>
        new(BrowserLoginOutcomeKind.StillLoggedOut, code, evidence.ToArray());

    public static BrowserLoginOutcome Rejected(string code, IEnumerable<string> evidence) =>
        new(BrowserLoginOutcomeKind.Rejected, code, evidence.ToArray());

    public static BrowserLoginOutcome Indeterminate(string code, IEnumerable<string> evidence) =>
        new(BrowserLoginOutcomeKind.Indeterminate, code, evidence.ToArray());
}

/// <summary>
/// Conservative post-submit judge. A dispatched submit is never accepted as authentication proof. Positive
/// authentication requires an explicit authenticated sentinel or configured authenticated URL/text marker,
/// while logout and boundary signals always take precedence.
/// </summary>
public static class BrowserLoginOutcomeJudge
{
    public static BrowserLoginOutcome Evaluate(
        ResearchBrowserPolicy policy,
        LoginRecipe recipe,
        BrowserSessionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(observation);

        try
        {
            policy.Validate();
            recipe.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BrowserLoginOutcome.Rejected("login_outcome_policy_or_recipe_invalid", new[] { ex.GetType().Name });
        }

        if (!string.Equals(policy.TargetId, recipe.TargetId, StringComparison.Ordinal))
        {
            return BrowserLoginOutcome.Rejected(
                "login_outcome_target_mismatch",
                new[] { "recipe target differs from active browser target" });
        }

        var navigation = ResearchBrowserNavigationGate.Evaluate(policy, observation.CurrentUri);
        if (!navigation.Allowed)
        {
            return BrowserLoginOutcome.Rejected(
                navigation.Code,
                new[] { "observation URI is outside the governed browser boundary" });
        }

        if (observation.ObservedAt == default)
        {
            return BrowserLoginOutcome.Rejected(
                "login_outcome_timestamp_missing",
                new[] { "observation has no timestamp" });
        }

        if (observation.RecentFirstPartyStatuses.Any(status => status is 401 or 403))
        {
            return BrowserLoginOutcome.LoggedOut(
                "login_rejected_by_first_party",
                new[] { "first-party response contained 401/403" });
        }

        if (observation.VisiblePasswordField)
        {
            return BrowserLoginOutcome.LoggedOut(
                "login_form_still_visible",
                new[] { "password field is still visible" });
        }

        var currentUrl = observation.CurrentUri.AbsoluteUri;
        var loggedOutUrl = FirstMatching(recipe.LoggedOutUrlMarkers, currentUrl);
        if (loggedOutUrl is not null)
        {
            return BrowserLoginOutcome.LoggedOut(
                "logged_out_url_marker_seen",
                new[] { "logged-out URL marker matched" });
        }

        var loggedOutText = FirstMatching(recipe.LoggedOutTextMarkers, observation.VisibleTextSample);
        if (loggedOutText is not null)
        {
            return BrowserLoginOutcome.LoggedOut(
                "logged_out_text_marker_seen",
                new[] { "logged-out text marker matched" });
        }

        var positiveEvidence = new List<string>();
        if (observation.AuthenticatedSentinelSeen)
        {
            positiveEvidence.Add("authenticated sentinel observed");
        }

        var authenticatedUrl = FirstMatching(recipe.AuthenticatedUrlMarkers, currentUrl);
        if (authenticatedUrl is not null)
        {
            positiveEvidence.Add("authenticated URL marker matched");
        }

        var authenticatedText = FirstMatching(recipe.AuthenticatedTextMarkers, observation.VisibleTextSample);
        if (authenticatedText is not null)
        {
            positiveEvidence.Add("authenticated text marker matched");
        }

        if (positiveEvidence.Count > 0)
        {
            return BrowserLoginOutcome.Authenticated(positiveEvidence);
        }

        return BrowserLoginOutcome.Indeterminate(
            "authentication_not_yet_proven",
            new[] { "no explicit authenticated sentinel or configured authenticated marker was observed" });
    }

    private static string? FirstMatching(IReadOnlyList<string> markers, string source)
    {
        ArgumentNullException.ThrowIfNull(markers);
        source ??= string.Empty;
        return markers.FirstOrDefault(marker =>
            !string.IsNullOrWhiteSpace(marker)
            && source.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
