using System.Text.Json;
using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class RepositoryIntelligenceTests
{
    [TestMethod]
    public void InitialCatalog_HasElevenUniqueFailClosedSeeds()
    {
        var seeds = RepositoryIntelligenceCatalog.InitialSeeds;

        Assert.AreEqual(11, seeds.Count);
        Assert.AreEqual(11, seeds.Select(seed => seed.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(seeds.All(seed => seed.GovernanceAuthority == RepositoryGovernanceAuthority.BootstrapProjection));
        Assert.IsTrue(seeds.All(seed => !seed.CanExecute()));
        Assert.IsTrue(seeds.All(seed => seed.SecurityReview == RepositorySecurityReviewState.NeedsReview));
        Assert.IsTrue(seeds.All(seed => !seed.RuntimeAllowlisted));
    }


    [TestMethod]
    public void BootstrapCatalog_CannotSelfAuthorizeAndMatchesCanonicalRepositorySetAndLicenseStatus()
    {
        var bootstrap = RepositoryIntelligenceCatalog.Find("ollama/ollama") with
        {
            PinnedRevision = "0123456789abcdef0123456789abcdef01234567",
            ContentSha256 = new string('a', 64),
            SecurityReview = RepositorySecurityReviewState.Approved,
            RuntimeAllowlisted = true,
            ManifestRuntimeApproval = "Approved"
        };

        Assert.IsFalse(bootstrap.CanExecute());
        Assert.Throws<InvalidOperationException>(bootstrap.Validate);

        using var manifest = JsonDocument.Parse(File.ReadAllText(FindCanonicalManifest()));
        var repositories = manifest.RootElement.GetProperty("repositories").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(
            repositories.Select(item => item.GetProperty("repository").GetString()!).ToArray(),
            RepositoryIntelligenceCatalog.InitialSeeds.Select(seed => seed.FullName).ToArray());

        foreach (var seed in RepositoryIntelligenceCatalog.InitialSeeds)
        {
            var entry = repositories.Single(item => string.Equals(
                item.GetProperty("repository").GetString(), seed.FullName, StringComparison.OrdinalIgnoreCase));
            var manifestLicense = entry.GetProperty("licenseSpdx").GetString();
            Assert.AreEqual(IsVerifiedLicense(manifestLicense), IsVerifiedLicense(seed.SpdxLicense), seed.FullName);
            Assert.IsFalse(seed.CanExecute(), seed.FullName);
        }
    }

    [TestMethod]
    public void AuditedManifestApproval_PreservesObservedPinSeparationAndMultipleModeFailClosedRules()
    {
        var approved = CreateApprovedRecord(RepositoryIntegrationMode.Adapter) with
        {
            ObservedRevision = "fedcba9876543210fedcba9876543210fedcba98",
            IntegrationModes = [RepositoryIntegrationMode.Adapter, RepositoryIntegrationMode.Reference]
        };

        approved.Validate();
        Assert.AreNotEqual(approved.ObservedRevision, approved.PinnedRevision);
        Assert.IsTrue(approved.CanExecute());

        var discoveryMixed = approved with
        {
            IntegrationModes = [RepositoryIntegrationMode.Adapter, RepositoryIntegrationMode.DiscoverySeed]
        };
        Assert.IsFalse(discoveryMixed.CanExecute());
        Assert.Throws<InvalidOperationException>(discoveryMixed.Validate);
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
    public void OllamaSeed_PreservesLocalOnlyAndModelAllowlistDenials()
    {
        var ollama = RepositoryIntelligenceCatalog.Find("ollama/ollama");

        CollectionAssert.Contains(ollama.DeniedCapabilities.ToArray(), "non-loopback-endpoint");
        CollectionAssert.Contains(ollama.DeniedCapabilities.ToArray(), "model-outside-allowlist");
        Assert.IsFalse(ollama.CanExecute());
    }

    [TestMethod]
    public void MicrosoftMxc_RemainsReferenceOnlyAndCannotExecute()
    {
        var mxc = RepositoryIntelligenceCatalog.Find("microsoft/mxc");

        Assert.AreEqual(RepositoryIntegrationMode.Reference, mxc.IntegrationMode);
        Assert.AreEqual("MIT", mxc.SpdxLicense);
        Assert.AreEqual(RepositorySecurityReviewState.NeedsReview, mxc.SecurityReview);
        Assert.IsFalse(mxc.RuntimeAllowlisted);
        Assert.IsNull(mxc.PinnedRevision);
        Assert.IsNull(mxc.ContentSha256);
        Assert.IsFalse(mxc.CanExecute());
        CollectionAssert.Contains(mxc.AllowedCapabilities.ToArray(), "sandbox-architecture-study");
        CollectionAssert.Contains(mxc.DeniedCapabilities.ToArray(), "runtime-dependency");
        CollectionAssert.Contains(mxc.DeniedCapabilities.ToArray(), "automatic-code-execution");
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
            DeniedCapabilities: [])
        {
            GovernanceAuthority = RepositoryGovernanceAuthority.AuditedManifest,
            ManifestRuntimeApproval = "Approved",
            ObservedRevision = "0123456789abcdef0123456789abcdef01234567",
            IntegrationModes = [mode]
        };
    }

    private static bool IsVerifiedLicense(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, "NOASSERTION", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(value, "UNKNOWN", StringComparison.OrdinalIgnoreCase);

    private static string FindCanonicalManifest()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, RepositoryIntelligenceCatalog.CanonicalManifestPath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Could not locate {RepositoryIntelligenceCatalog.CanonicalManifestPath}.");
    }
}
