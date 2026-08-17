namespace Aevrix.Core;

public enum LoginAuthenticationOutcomeStatus
{
    Rejected,
    AuthenticationFailed,
    NeedsConfirmation,
    Authenticated
}

public sealed record LoginAuthenticationOutcome(
    LoginAuthenticationOutcomeStatus Status,
    string Code,
    string Detail)
{
    public static LoginAuthenticationOutcome Rejected(string code, string detail) =>
        new(LoginAuthenticationOutcomeStatus.Rejected, code, detail);

    public static LoginAuthenticationOutcome Failed(string code, string detail) =>
        new(LoginAuthenticationOutcomeStatus.AuthenticationFailed, code, detail);

    public static LoginAuthenticationOutcome NeedsConfirmation(string code, string detail) =>
        new(LoginAuthenticationOutcomeStatus.NeedsConfirmation, code, detail);

    public static LoginAuthenticationOutcome Authenticated(string code, string detail) =>
        new(LoginAuthenticationOutcomeStatus.Authenticated, code, detail);
}

/// <summary>
/// Evaluates browser evidence after a login submission without treating navigation alone as proof of authentication.
/// A confirmed state requires a previously governed authenticated URL/text marker. A clean transition away from the
/// login page without such a marker remains NeedsConfirmation so first-time learning cannot silently promote trust.
/// </summary>
public static class LoginAuthenticationOutcomeJudge
{
    public static LoginAuthenticationOutcome Evaluate(
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
            return LoginAuthenticationOutcome.Rejected(
                "login_outcome_configuration_invalid",
                "Browser policy or login recipe is invalid.");
        }

        if (!string.Equals(policy.TargetId, recipe.TargetId, StringComparison.Ordinal))
        {
            return LoginAuthenticationOutcome.Rejected(
                "login_outcome_target_mismatch",
                "Login recipe target does not match the active Research Browser target.");
        }

        if (observation.ObservedAt == default)
        {
            return LoginAuthenticationOutcome.Rejected(
                "login_outcome_timestamp_missing",
                "Post-login browser observation has no timestamp.");
        }

        var navigation = ResearchBrowserNavigationGate.Evaluate(policy, observation.CurrentUri);
        if (!navigation.Allowed)
        {
            return LoginAuthenticationOutcome.Rejected(
                navigation.Code,
                "Post-login page is outside the governed browser boundary.");
        }

        if (observation.RecentFirstPartyStatuses.Any(status => status is 401 or 403))
        {
            return LoginAuthenticationOutcome.Failed(
                "login_outcome_first_party_denied",
                "A first-party request returned 401/403 after login submission.");
        }

        if (observation.VisiblePasswordField)
        {
            return LoginAuthenticationOutcome.Failed(
                "login_outcome_password_field_visible",
                "A password field is still visible after login submission.");
        }

        if (MatchesAny(observation.CurrentUri.AbsoluteUri, recipe.LoggedOutUrlMarkers)
            || MatchesAny(observation.VisibleTextSample, recipe.LoggedOutTextMarkers))
        {
            return LoginAuthenticationOutcome.Failed(
                "login_outcome_logged_out_marker",
                "A governed logged-out marker is still present after login submission.");
        }

        string currentCanonical;
        string loginCanonical;
        try
        {
            currentCanonical = ProjectCredentialVault.CanonicalizeLoginUri(observation.CurrentUri);
            loginCanonical = ProjectCredentialVault.CanonicalizeLoginUri(recipe.LoginUri);
        }
        catch (ArgumentException)
        {
            return LoginAuthenticationOutcome.Rejected(
                "login_outcome_uri_invalid",
                "Post-login or recipe URI could not be canonicalized.");
        }

        if (string.Equals(currentCanonical, loginCanonical, StringComparison.Ordinal))
        {
            return LoginAuthenticationOutcome.Failed(
                "login_outcome_still_on_login_page",
                "Navigation remains on the canonical login page after submission.");
        }

        var authenticatedMarkerConfigured =
            recipe.AuthenticatedUrlMarkers.Count > 0 || recipe.AuthenticatedTextMarkers.Count > 0;
        var authenticatedMarkerSeen =
            MatchesAny(observation.CurrentUri.AbsoluteUri, recipe.AuthenticatedUrlMarkers)
            || MatchesAny(observation.VisibleTextSample, recipe.AuthenticatedTextMarkers);

        if (authenticatedMarkerSeen)
        {
            return LoginAuthenticationOutcome.Authenticated(
                "login_outcome_authenticated_marker",
                "A previously governed authenticated marker was observed after login submission.");
        }

        if (authenticatedMarkerConfigured)
        {
            return LoginAuthenticationOutcome.NeedsConfirmation(
                "login_outcome_authenticated_marker_missing",
                "The page left the login URL but no configured authenticated marker was observed.");
        }

        return LoginAuthenticationOutcome.NeedsConfirmation(
            "login_outcome_first_success_requires_confirmation",
            "The page left the login URL without obvious failure, but no governed authenticated marker exists yet.");
    }

    private static bool MatchesAny(string source, IReadOnlyList<string> markers)
    {
        if (string.IsNullOrEmpty(source) || markers.Count == 0)
        {
            return false;
        }

        return markers.Any(marker =>
            !string.IsNullOrWhiteSpace(marker)
            && source.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}