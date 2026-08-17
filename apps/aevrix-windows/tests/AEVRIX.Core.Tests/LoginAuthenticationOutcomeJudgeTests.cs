using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class LoginAuthenticationOutcomeJudgeTests
{
    [TestMethod]
    public void Evaluate_401Or403IsAuthenticationFailure()
    {
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            Recipe(),
            Observation("https://example.com/app", statuses: new[] { 401 }));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.AuthenticationFailed, result.Status);
        Assert.AreEqual("login_outcome_first_party_denied", result.Code);
    }

    [TestMethod]
    public void Evaluate_PasswordFieldStillVisibleIsAuthenticationFailure()
    {
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            Recipe(),
            Observation("https://example.com/app", visiblePassword: true));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.AuthenticationFailed, result.Status);
        Assert.AreEqual("login_outcome_password_field_visible", result.Code);
    }

    [TestMethod]
    public void Evaluate_RemainingOnCanonicalLoginPageIsFailureEvenWhenQueryChanged()
    {
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            Recipe(),
            Observation("https://EXAMPLE.com:443/login?error=1#form"));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.AuthenticationFailed, result.Status);
        Assert.AreEqual("login_outcome_still_on_login_page", result.Code);
    }

    [TestMethod]
    public void Evaluate_LoggedOutTextMarkerIsFailure()
    {
        var recipe = Recipe() with { LoggedOutTextMarkers = new[] { "Sign in again" } };
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            recipe,
            Observation("https://example.com/app", visibleText: "Session expired. Sign in again."));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.AuthenticationFailed, result.Status);
        Assert.AreEqual("login_outcome_logged_out_marker", result.Code);
    }

    [TestMethod]
    public void Evaluate_PreviouslyGovernedAuthenticatedUrlMarkerConfirmsSuccess()
    {
        var recipe = Recipe() with { AuthenticatedUrlMarkers = new[] { "/dashboard" } };
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            recipe,
            Observation("https://example.com/dashboard?view=1"));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.Authenticated, result.Status);
        Assert.AreEqual("login_outcome_authenticated_marker", result.Code);
    }

    [TestMethod]
    public void Evaluate_PreviouslyGovernedAuthenticatedTextMarkerConfirmsSuccess()
    {
        var recipe = Recipe() with { AuthenticatedTextMarkers = new[] { "My account" } };
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            recipe,
            Observation("https://example.com/app", visibleText: "Welcome. My account"));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.Authenticated, result.Status);
    }

    [TestMethod]
    public void Evaluate_FirstCleanTransitionWithoutMarkersNeedsConfirmation()
    {
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            Recipe(),
            Observation("https://example.com/app"));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.NeedsConfirmation, result.Status);
        Assert.AreEqual("login_outcome_first_success_requires_confirmation", result.Code);
    }

    [TestMethod]
    public void Evaluate_ConfiguredMarkerMissingNeedsConfirmationInsteadOfSuccess()
    {
        var recipe = Recipe() with { AuthenticatedUrlMarkers = new[] { "/dashboard" } };
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            recipe,
            Observation("https://example.com/app"));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.NeedsConfirmation, result.Status);
        Assert.AreEqual("login_outcome_authenticated_marker_missing", result.Code);
    }

    [TestMethod]
    public void Evaluate_PostLoginPageOutsideAllowlistIsRejected()
    {
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            Recipe(),
            Observation("https://other.example/app"));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.Rejected, result.Status);
        Assert.AreEqual("navigation_host_not_allowed", result.Code);
    }

    [TestMethod]
    public void Evaluate_TargetMismatchIsRejected()
    {
        var recipe = Recipe() with { TargetId = "target-other" };
        var result = LoginAuthenticationOutcomeJudge.Evaluate(
            Policy(),
            recipe,
            Observation("https://example.com/app"));

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.Rejected, result.Status);
        Assert.AreEqual("login_outcome_target_mismatch", result.Code);
    }

    [TestMethod]
    public void Evaluate_MissingTimestampIsRejected()
    {
        var observation = Observation("https://example.com/app") with { ObservedAt = default };
        var result = LoginAuthenticationOutcomeJudge.Evaluate(Policy(), Recipe(), observation);

        Assert.AreEqual(LoginAuthenticationOutcomeStatus.Rejected, result.Status);
        Assert.AreEqual("login_outcome_timestamp_missing", result.Code);
    }

    private static ResearchBrowserPolicy Policy() => new ResearchBrowserPolicy(
        TargetId: "target-web",
        AllowedHosts: new[] { "example.com" },
        PersistTargetProfile: true,
        RememberCredentials: true,
        AutomaticRelogin: false,
        PauseImmediatelyOnLogout: true,
        ShortWindowFailureThreshold: 3,
        FailureWindow: TimeSpan.FromMinutes(15),
        Cooldown: TimeSpan.FromMinutes(10),
        ClearSiteDataWhenProjectDeleted: true,
        EgressPolicy: EgressPolicy.Offline()).Validate();

    private static LoginRecipe Recipe() => new(
        TargetId: "target-web",
        LoginUri: new Uri("https://example.com/login"),
        UsernameSelector: "#user",
        PasswordSelector: "#secret",
        SubmitSelector: "#submit",
        AuthenticatedUrlMarkers: Array.Empty<string>(),
        AuthenticatedTextMarkers: Array.Empty<string>(),
        LoggedOutUrlMarkers: new[] { "/login" },
        LoggedOutTextMarkers: Array.Empty<string>(),
        LearnedAt: DateTimeOffset.UtcNow);

    private static BrowserSessionObservation Observation(
        string uri,
        bool visiblePassword = false,
        IReadOnlyList<int>? statuses = null,
        string visibleText = "") => new(
            CurrentUri: new Uri(uri),
            VisiblePasswordField: visiblePassword,
            RecentFirstPartyStatuses: statuses ?? Array.Empty<int>(),
            VisibleTextSample: visibleText,
            AuthenticatedSentinelSeen: false,
            ObservedAt: DateTimeOffset.UtcNow);
}