using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class BrowserLoginOutcomeJudgeTests
{
    [TestMethod]
    public void Evaluate_SubmitWithoutPositiveEvidenceRemainsIndeterminate()
    {
        var outcome = BrowserLoginOutcomeJudge.Evaluate(
            Policy(),
            Recipe(),
            Observation(new Uri("https://example.com/app")));

        Assert.AreEqual(BrowserLoginOutcomeKind.Indeterminate, outcome.Kind);
        Assert.AreEqual("authentication_not_yet_proven", outcome.Code);
    }

    [TestMethod]
    public void Evaluate_AuthenticatedSentinelProvesAuthentication()
    {
        var outcome = BrowserLoginOutcomeJudge.Evaluate(
            Policy(),
            Recipe(),
            Observation(new Uri("https://example.com/app"), authenticatedSentinelSeen: true));

        Assert.AreEqual(BrowserLoginOutcomeKind.Authenticated, outcome.Kind);
        Assert.IsTrue(outcome.Evidence.Any(item => item.Contains("sentinel", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Evaluate_AuthenticatedUrlMarkerProvesAuthentication()
    {
        var outcome = BrowserLoginOutcomeJudge.Evaluate(
            Policy(),
            Recipe(authenticatedUrlMarkers: new[] { "/dashboard" }),
            Observation(new Uri("https://example.com/dashboard")));

        Assert.AreEqual(BrowserLoginOutcomeKind.Authenticated, outcome.Kind);
    }

    [TestMethod]
    public void Evaluate_AuthenticatedTextMarkerProvesAuthentication()
    {
        var outcome = BrowserLoginOutcomeJudge.Evaluate(
            Policy(),
            Recipe(authenticatedTextMarkers: new[] { "Welcome back" }),
            Observation(new Uri("https://example.com/app"), visibleText: "Welcome back, operator"));

        Assert.AreEqual(BrowserLoginOutcomeKind.Authenticated, outcome.Kind);
    }

    [TestMethod]
    public void Evaluate_VisiblePasswordFieldOverridesPositiveEvidence()
    {
        var outcome = BrowserLoginOutcomeJudge.Evaluate(
            Policy(),
            Recipe(authenticatedUrlMarkers: new[] { "/app" }),
            Observation(
                new Uri("https://example.com/app"),
                visiblePasswordField: true,
                authenticatedSentinelSeen: true));

        Assert.AreEqual(BrowserLoginOutcomeKind.StillLoggedOut, outcome.Kind);
        Assert.AreEqual("login_form_still_visible", outcome.Code);
    }

    [TestMethod]
    public void Evaluate_FirstParty401OverridesPositiveMarker()
    {
        var outcome = BrowserLoginOutcomeJudge.Evaluate(
            Policy(),
            Recipe(authenticatedUrlMarkers: new[] { "/app" }),
            Observation(
                new Uri("https://example.com/app"),
                statuses: new[] { 200, 401 },
                authenticatedSentinelSeen: true));

        Assert.AreEqual(BrowserLoginOutcomeKind.StillLoggedOut, outcome.Kind);
        Assert.AreEqual("login_rejected_by_first_party", outcome.Code);
    }

    [TestMethod]
    public void Evaluate_LoggedOutMarkerOverridesPositiveMarker()
    {
        var recipe = Recipe(
            authenticatedUrlMarkers: new[] { "/login?state=ok" },
            loggedOutUrlMarkers: new[] { "/login" });
        var outcome = BrowserLoginOutcomeJudge.Evaluate(
            Policy(),
            recipe,
            Observation(new Uri("https://example.com/login?state=ok"), authenticatedSentinelSeen: true));

        Assert.AreEqual(BrowserLoginOutcomeKind.StillLoggedOut, outcome.Kind);
        Assert.AreEqual("logged_out_url_marker_seen", outcome.Code);
    }

    [TestMethod]
    public void Evaluate_OutsideAllowlistIsRejected()
    {
        var outcome = BrowserLoginOutcomeJudge.Evaluate(
            Policy(),
            Recipe(),
            Observation(new Uri("https://other.example/app"), authenticatedSentinelSeen: true));

        Assert.AreEqual(BrowserLoginOutcomeKind.Rejected, outcome.Kind);
        Assert.AreEqual("navigation_host_not_allowed", outcome.Code);
    }

    [TestMethod]
    public void Evaluate_TargetMismatchIsRejected()
    {
        var outcome = BrowserLoginOutcomeJudge.Evaluate(
            Policy(),
            Recipe(targetId: "target:other"),
            Observation(new Uri("https://example.com/app"), authenticatedSentinelSeen: true));

        Assert.AreEqual(BrowserLoginOutcomeKind.Rejected, outcome.Kind);
        Assert.AreEqual("login_outcome_target_mismatch", outcome.Code);
    }

    private static ResearchBrowserPolicy Policy() => new ResearchBrowserPolicy(
        TargetId: "target:web",
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

    private static LoginRecipe Recipe(
        string targetId = "target:web",
        IReadOnlyList<string>? authenticatedUrlMarkers = null,
        IReadOnlyList<string>? authenticatedTextMarkers = null,
        IReadOnlyList<string>? loggedOutUrlMarkers = null,
        IReadOnlyList<string>? loggedOutTextMarkers = null) => new(
        TargetId: targetId,
        LoginUri: new Uri("https://example.com/login"),
        UsernameSelector: "#user",
        PasswordSelector: "#secret",
        SubmitSelector: "#submit",
        AuthenticatedUrlMarkers: authenticatedUrlMarkers ?? Array.Empty<string>(),
        AuthenticatedTextMarkers: authenticatedTextMarkers ?? Array.Empty<string>(),
        LoggedOutUrlMarkers: loggedOutUrlMarkers ?? new[] { "/login" },
        LoggedOutTextMarkers: loggedOutTextMarkers ?? new[] { "Sign in" },
        LearnedAt: DateTimeOffset.UtcNow);

    private static BrowserSessionObservation Observation(
        Uri uri,
        bool visiblePasswordField = false,
        IReadOnlyList<int>? statuses = null,
        string visibleText = "",
        bool authenticatedSentinelSeen = false) => new(
        CurrentUri: uri,
        VisiblePasswordField: visiblePasswordField,
        RecentFirstPartyStatuses: statuses ?? new[] { 200 },
        VisibleTextSample: visibleText,
        AuthenticatedSentinelSeen: authenticatedSentinelSeen,
        ObservedAt: DateTimeOffset.UtcNow);
}
