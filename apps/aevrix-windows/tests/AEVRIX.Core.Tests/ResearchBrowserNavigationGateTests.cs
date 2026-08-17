using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ResearchBrowserNavigationGateTests
{
    [TestMethod]
    public void Evaluate_AllowsExactHttpsHostAndTransientUrlParts()
    {
        var decision = ResearchBrowserNavigationGate.Evaluate(
            Policy("portal.example.com"),
            new Uri("https://PORTAL.example.com/login?return=%2Fapp#form"));

        Assert.IsTrue(decision.Allowed);
        Assert.AreEqual("navigation_allowed", decision.Code);
    }

    [TestMethod]
    public void Evaluate_BlocksHttpBeforeNavigation()
    {
        var decision = ResearchBrowserNavigationGate.Evaluate(
            Policy("portal.example.com"),
            new Uri("http://portal.example.com/login"));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("navigation_requires_https", decision.Code);
    }

    [TestMethod]
    public void Evaluate_BlocksSubdomainWhenOnlyParentHostIsAllowed()
    {
        var decision = ResearchBrowserNavigationGate.Evaluate(
            Policy("example.com"),
            new Uri("https://auth.example.com/login"));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("navigation_host_not_allowed", decision.Code);
    }

    [TestMethod]
    public void Evaluate_BlocksEmbeddedCredentials()
    {
        var decision = ResearchBrowserNavigationGate.Evaluate(
            Policy("example.com"),
            new Uri("https://user:password@example.com/login"));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("navigation_embedded_credentials_forbidden", decision.Code);
    }

    [TestMethod]
    public void Evaluate_BlocksAlternateHttpsPortWithoutExplicitPortPolicyModel()
    {
        var decision = ResearchBrowserNavigationGate.Evaluate(
            Policy("example.com"),
            new Uri("https://example.com:8443/login"));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("navigation_non_default_https_port", decision.Code);
    }

    [TestMethod]
    public void Evaluate_BlocksHostOutsideAllowlist()
    {
        var decision = ResearchBrowserNavigationGate.Evaluate(
            Policy("example.com"),
            new Uri("https://other.example/login"));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("navigation_host_not_allowed", decision.Code);
    }

    [TestMethod]
    public void Evaluate_InvalidPolicyFailsClosed()
    {
        var invalid = Policy("example.com") with { PauseImmediatelyOnLogout = false };

        var decision = ResearchBrowserNavigationGate.Evaluate(
            invalid,
            new Uri("https://example.com/login"));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("browser_policy_invalid", decision.Code);
    }

    private static ResearchBrowserPolicy Policy(params string[] hosts) => new(
        TargetId: "target-web",
        AllowedHosts: hosts,
        PersistTargetProfile: true,
        RememberCredentials: true,
        AutomaticRelogin: false,
        PauseImmediatelyOnLogout: true,
        ShortWindowFailureThreshold: 3,
        FailureWindow: TimeSpan.FromMinutes(15),
        Cooldown: TimeSpan.FromMinutes(10),
        ClearSiteDataWhenProjectDeleted: true,
        EgressPolicy: EgressPolicy.Offline());
}