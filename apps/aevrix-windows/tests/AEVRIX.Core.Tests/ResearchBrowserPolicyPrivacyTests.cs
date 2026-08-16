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
