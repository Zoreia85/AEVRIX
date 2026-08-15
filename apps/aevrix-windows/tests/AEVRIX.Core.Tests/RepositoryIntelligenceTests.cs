using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class RepositoryIntelligenceTests
{
    [TestMethod]
    public void InitialCatalog_HasTenUniqueFailClosedSeeds()
    {
        var seeds = RepositoryIntelligenceCatalog.InitialSeeds;

        Assert.AreEqual(10, seeds.Count);
        Assert.AreEqual(10, seeds.Select(seed => seed.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(seeds.All(seed => !seed.CanExecute()));
        Assert.IsTrue(seeds.All(seed => seed.SecurityReview == RepositorySecurityReviewState.NeedsReview));
        Assert.IsTrue(seeds.All(seed => !seed.RuntimeAllowlisted));
    }

    [TestMethod]
    public void ScraplingSeed_ExplicitlyDeniesEvasionCapabilities()
    {
        var scrapling = RepositoryIntelligenceCatalog.Find("D4Vinci/Scrapling");

        CollectionAssert.Contains(scrapling.DeniedCapabilities.ToArray(), "anti-bot-bypass");
        CollectionAssert.Contains(scrapling.DeniedCapabilities.ToArray(), "captcha-bypass");
        CollectionAssert.Contains(scrapling.DeniedCapabilities.ToArray(), "cloudflare-bypass");
        CollectionAssert.Contains(scrapling.DeniedCapabilities.ToArray(), "access-control-evasion");
    }

    [TestMethod]
    public void DiscoverySeed_CannotExecuteEvenWhenOtherFieldsAreApproved()
    {
        var record = CreateApprovedRecord(RepositoryIntegrationMode.DiscoverySeed);

        Assert.IsFalse(record.CanExecute());
    }

    [TestMethod]
    public void ExecutableAdapter_RequiresPinChecksumLicenseReviewAndAllowlist()
    {
        var candidate = CreateApprovedRecord(RepositoryIntegrationMode.Adapter) with
        {
            RuntimeAllowlisted = false
        };

        Assert.IsFalse(candidate.CanExecute());

        var approved = candidate with { RuntimeAllowlisted = true };
        approved.Validate();
        Assert.IsTrue(approved.CanExecute());

        Assert.IsFalse((approved with { PinnedRevision = null, RuntimeAllowlisted = false }).CanExecute());
        Assert.IsFalse((approved with { ContentSha256 = null, RuntimeAllowlisted = false }).CanExecute());
        Assert.IsFalse((approved with { SpdxLicense = null, RuntimeAllowlisted = false }).CanExecute());
        Assert.IsFalse((approved with { SecurityReview = RepositorySecurityReviewState.NeedsReview, RuntimeAllowlisted = false }).CanExecute());
    }

    [TestMethod]
    public void RuntimeAllowlist_FailsClosedWhenExecutableGatesAreIncomplete()
    {
        var incomplete = RepositoryIntelligenceCatalog.Find("ollama/ollama") with
        {
            RuntimeAllowlisted = true
        };

        Assert.Throws<InvalidOperationException>(incomplete.Validate);
    }

    [TestMethod]
    public void CanonicalUrl_MustBeExactHttpsGithubRepositoryUrl()
    {
        var invalid = CreateApprovedRecord(RepositoryIntegrationMode.Adapter) with
        {
            CanonicalUrl = new Uri("https://github.com/example/tool?ref=unsafe", UriKind.Absolute),
            RuntimeAllowlisted = false
        };

        Assert.Throws<ArgumentException>(invalid.Validate);
    }

    [TestMethod]
    public void FreeForDev_RemainsReferenceWithoutAssertedLicense()
    {
        var record = RepositoryIntelligenceCatalog.Find("ripienaar/free-for-dev");

        Assert.AreEqual(RepositoryIntegrationMode.Reference, record.IntegrationMode);
        Assert.IsNull(record.SpdxLicense);
        Assert.IsFalse(record.CanExecute());
    }

    private static RepositoryIntelligenceRecord CreateApprovedRecord(RepositoryIntegrationMode mode)
    {
        return new RepositoryIntelligenceRecord(
            Owner: "example",
            Name: "tool",
            CanonicalUrl: new Uri("https://github.com/example/tool", UriKind.Absolute),
            Purpose: "Test tool",
            IntegrationMode: mode,
            SpdxLicense: "MIT",
            PinnedRevision: "0123456789abcdef0123456789abcdef01234567",
            ContentSha256: new string('a', 64),
            SecurityReview: RepositorySecurityReviewState.Approved,
            RuntimeAllowlisted: true,
            LastVerifiedAt: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
            AllowedCapabilities: ["test"],
            DeniedCapabilities: []);
    }
}
