using Aevrix.Core;

namespace AEVRIX.Core.Tests;

[TestClass]
public sealed class ResearchBrowserPolicyPrivacyTests
{
    [TestMethod]
    public void SecureDefault_IsEphemeralAndClearsProjectState()
    {
        var policy = ResearchBrowserPolicy.SecureDefault(
            "target:web",
            new[] { "example.com" },
            EgressPolicy.Offline());

        Assert.IsFalse(policy.PersistTargetProfile);
        Assert.IsFalse(policy.RememberCredentials);
        Assert.IsFalse(policy.AutomaticRelogin);
        Assert.IsTrue(policy.PauseImmediatelyOnLogout);
        Assert.IsTrue(policy.ClearSiteDataWhenProjectDeleted);
    }

    [TestMethod]
    public void Validate_RejectsCredentialPersistenceWithoutPersistentProfile()
    {
        var policy = CreatePolicy() with
        {
            PersistTargetProfile = false,
            RememberCredentials = true
        };

        Assert.Throws<InvalidOperationException>(() => policy.Validate());
    }

    [TestMethod]
    public void Validate_RejectsAutomaticReloginWithoutCredentialPersistence()
    {
        var policy = CreatePolicy() with
        {
            PersistTargetProfile = true,
            RememberCredentials = false,
            AutomaticRelogin = true
        };

        Assert.Throws<InvalidOperationException>(() => policy.Validate());
    }

    [TestMethod]
    public void Validate_RejectsPersistentStateWithoutProjectDeletionCleanup()
    {
        var policy = CreatePolicy() with
        {
            PersistTargetProfile = true,
            RememberCredentials = true,
            AutomaticRelogin = true,
            ClearSiteDataWhenProjectDeleted = false
        };

        Assert.Throws<InvalidOperationException>(() => policy.Validate());
    }

    [TestMethod]
    public void Validate_RejectsWildcardTargetHost()
    {
        var policy = CreatePolicy() with
        {
            AllowedHosts = new[] { "*.example.com" }
        };

        Assert.Throws<InvalidOperationException>(() => policy.Validate());
    }

    [TestMethod]
    public void Evaluate_RejectsRecipeForDifferentTarget()
    {
        var guard = new BrowserSessionGuard(CreatePolicy());
        var decision = guard.Evaluate(
            HealthyObservation("https://example.com/app"),
            CreateRecipe(targetId: "target:other", loginUri: "https://example.com/login"),
            DateTimeOffset.UtcNow);

        Assert.AreEqual(BrowserSessionDecisionKind.OpenCircuitBreaker, decision.Kind);
        StringAssert.Contains(decision.Reason, "target");
    }

    [TestMethod]
    public void Evaluate_RejectsRecipeHostOutsideAllowlist()
    {
        var guard = new BrowserSessionGuard(CreatePolicy());
        var decision = guard.Evaluate(
            HealthyObservation("https://example.com/app"),
            CreateRecipe(loginUri: "https://other.example/login"),
            DateTimeOffset.UtcNow);

        Assert.AreEqual(BrowserSessionDecisionKind.OpenCircuitBreaker, decision.Kind);
        StringAssert.Contains(decision.Reason, "allowlist");
    }

    [TestMethod]
    public void Evaluate_RejectsObservedRedirectOutsideAllowlist()
    {
        var guard = new BrowserSessionGuard(CreatePolicy());
        var decision = guard.Evaluate(
            HealthyObservation("https://other.example/app"),
            CreateRecipe(),
            DateTimeOffset.UtcNow);

        Assert.AreEqual(BrowserSessionDecisionKind.OpenCircuitBreaker, decision.Kind);
        StringAssert.Contains(decision.Reason, "allowlist");
    }

    [TestMethod]
    public void Evaluate_AllowsHealthyHttpsObservationInsideAllowlist()
    {
        var guard = new BrowserSessionGuard(CreatePolicy());
        var decision = guard.Evaluate(
            HealthyObservation("https://example.com/app"),
            CreateRecipe(),
            DateTimeOffset.UtcNow);

        Assert.AreEqual(BrowserSessionDecisionKind.Continue, decision.Kind);
    }

    private static LoginRecipe CreateRecipe(
        string targetId = "target:web",
        string loginUri = "https://example.com/login") => new(
        TargetId: targetId,
        LoginUri: new Uri(loginUri),
        UsernameSelector: "#username",
        PasswordSelector: "#password",
        SubmitSelector: "#submit",
        AuthenticatedUrlMarkers: new[] { "/app" },
        AuthenticatedTextMarkers: Array.Empty<string>(),
        LoggedOutUrlMarkers: new[] { "/login" },
        LoggedOutTextMarkers: Array.Empty<string>(),
        LearnedAt: DateTimeOffset.UtcNow);

    private static BrowserSessionObservation HealthyObservation(string uri) => new(
        CurrentUri: new Uri(uri),
        VisiblePasswordField: false,
        RecentFirstPartyStatuses: Array.Empty<int>(),
        VisibleTextSample: "healthy",
        AuthenticatedSentinelSeen: true,
        ObservedAt: DateTimeOffset.UtcNow);

    private static ResearchBrowserPolicy CreatePolicy() => new(
        TargetId: "target:web",
        AllowedHosts: new[] { "example.com" },
        PersistTargetProfile: false,
        RememberCredentials: false,
        AutomaticRelogin: false,
        PauseImmediatelyOnLogout: true,
        ShortWindowFailureThreshold: 3,
        FailureWindow: TimeSpan.FromMinutes(15),
        Cooldown: TimeSpan.FromMinutes(10),
        ClearSiteDataWhenProjectDeleted: true,
        EgressPolicy: EgressPolicy.Offline());
}
