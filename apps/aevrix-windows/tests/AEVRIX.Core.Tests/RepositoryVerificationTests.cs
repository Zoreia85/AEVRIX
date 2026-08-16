using Aevrix.Core;

namespace AEVRIX.Core.Tests;

[TestClass]
public sealed class RepositoryVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 17, 55, 0, TimeSpan.Zero);

    [TestMethod]
    public void ExecutableSeedWithoutPinHashOrApprovalFailsClosed()
    {
        var expected = RepositoryIntelligenceCatalog.Find("ollama/ollama");
        var observed = Observation(
            expected.FullName,
            expected.CanonicalUrl,
            "0123456789abcdef0123456789abcdef01234567",
            "MIT");

        var report = RepositoryProvenanceVerifier.Verify(expected, observed, Now);

        Assert.IsTrue(report.HasBlockers);
        CollectionAssert.IsSubsetOf(
            new[]
            {
                "repository.security-review.required",
                "repository.runtime-allowlist.required",
                "repository.pin.required",
                "repository.hash.required"
            },
            report.Findings.Select(finding => finding.Code).ToArray());
    }

    [TestMethod]
    public void ApprovedPinnedRuntimeWithMatchingHashPassesProvenanceGates()
    {
        var hash = new string('b', 64);
        var revision = "0123456789abcdef0123456789abcdef01234567";
        var expected = new RepositoryIntelligenceRecord(
            Owner: "example",
            Name: "runtime",
            CanonicalUrl: new Uri("https://github.com/example/runtime", UriKind.Absolute),
            Purpose: "Verified runtime fixture",
            IntegrationMode: RepositoryIntegrationMode.Adapter,
            SpdxLicense: "MIT",
            PinnedRevision: revision,
            ContentSha256: hash,
            SecurityReview: RepositorySecurityReviewState.Approved,
            RuntimeAllowlisted: true,
            LastVerifiedAt: Now,
            AllowedCapabilities: new[] { "analysis" },
            DeniedCapabilities: Array.Empty<string>());
        var observed = Observation(expected.FullName, expected.CanonicalUrl, revision, "MIT", hash);

        var report = RepositoryProvenanceVerifier.Verify(expected, observed, Now);

        Assert.IsFalse(report.HasBlockers);
        Assert.IsTrue(report.CanRemainRuntimeEligible);
        Assert.AreEqual("repository.provenance.verified", report.Findings.Single().Code);
    }

    [TestMethod]
    public void LicenseDriftBlocksRuntimeEligibility()
    {
        var expected = RepositoryIntelligenceCatalog.Find("langflow-ai/langflow");
        var observed = Observation(
            expected.FullName,
            expected.CanonicalUrl,
            "0123456789abcdef0123456789abcdef01234567",
            "GPL-3.0-only");

        var report = RepositoryProvenanceVerifier.Verify(expected, observed, Now);

        Assert.IsTrue(report.HasBlockers);
        Assert.IsTrue(report.Findings.Any(finding => finding.Code == "repository.license.drift"));
    }

    [TestMethod]
    public void ArchivedRepositoryIsBlocked()
    {
        var expected = RepositoryIntelligenceCatalog.Find("sindresorhus/awesome");
        var observed = new RepositoryObservation(
            FullName: expected.FullName,
            CanonicalUrl: expected.CanonicalUrl,
            DefaultBranch: "main",
            HeadRevision: "0123456789abcdef0123456789abcdef01234567",
            SpdxLicense: "CC0-1.0",
            Archived: true,
            ObservedAt: Now,
            ContentSha256: null);

        var report = RepositoryProvenanceVerifier.Verify(expected, observed, Now);

        Assert.IsTrue(report.HasBlockers);
        Assert.IsTrue(report.Findings.Any(finding => finding.Code == "repository.archived"));
        Assert.IsTrue(report.Findings.Any(finding => finding.Code == "repository.non-executable-by-design"));
    }

    [TestMethod]
    public void RevisionDriftWarnsButDoesNotOverridePinnedRuntimeDecisionByItself()
    {
        var hash = new string('c', 64);
        var expected = new RepositoryIntelligenceRecord(
            Owner: "example",
            Name: "runtime",
            CanonicalUrl: new Uri("https://github.com/example/runtime", UriKind.Absolute),
            Purpose: "Verified runtime fixture",
            IntegrationMode: RepositoryIntegrationMode.OptionalTool,
            SpdxLicense: "MIT",
            PinnedRevision: "0123456789abcdef0123456789abcdef01234567",
            ContentSha256: hash,
            SecurityReview: RepositorySecurityReviewState.Approved,
            RuntimeAllowlisted: true,
            LastVerifiedAt: Now,
            AllowedCapabilities: new[] { "analysis" },
            DeniedCapabilities: Array.Empty<string>());
        var observed = Observation(
            expected.FullName,
            expected.CanonicalUrl,
            "fedcba9876543210fedcba9876543210fedcba98",
            "MIT",
            hash);

        var report = RepositoryProvenanceVerifier.Verify(expected, observed, Now);

        Assert.IsFalse(report.HasBlockers);
        Assert.IsTrue(report.Findings.Any(finding => finding.Code == "repository.revision.drift"));
    }

    [TestMethod]
    public void AbbreviatedRevisionCannotAuthorizeExecutableRuntime()
    {
        var record = new RepositoryIntelligenceRecord(
            Owner: "example",
            Name: "runtime",
            CanonicalUrl: new Uri("https://github.com/example/runtime", UriKind.Absolute),
            Purpose: "Abbreviated pin fixture",
            IntegrationMode: RepositoryIntegrationMode.Adapter,
            SpdxLicense: "MIT",
            PinnedRevision: "0123456",
            ContentSha256: new string('a', 64),
            SecurityReview: RepositorySecurityReviewState.Approved,
            RuntimeAllowlisted: false,
            LastVerifiedAt: Now,
            AllowedCapabilities: new[] { "analysis" },
            DeniedCapabilities: Array.Empty<string>());

        Assert.IsFalse(record.CanExecute());
        AssertThrows<InvalidOperationException>(() => (record with { RuntimeAllowlisted = true }).Validate());
    }

    [TestMethod]
    public void PlaceholderLicenseCannotAuthorizeExecutableRuntime()
    {
        foreach (var license in new[] { "NOASSERTION", "NONE", "UNKNOWN" })
        {
            var record = new RepositoryIntelligenceRecord(
                Owner: "example",
                Name: "runtime",
                CanonicalUrl: new Uri("https://github.com/example/runtime", UriKind.Absolute),
                Purpose: "License placeholder fixture",
                IntegrationMode: RepositoryIntegrationMode.OptionalTool,
                SpdxLicense: license,
                PinnedRevision: "0123456789abcdef0123456789abcdef01234567",
                ContentSha256: new string('d', 64),
                SecurityReview: RepositorySecurityReviewState.Approved,
                RuntimeAllowlisted: false,
                LastVerifiedAt: Now,
                AllowedCapabilities: new[] { "analysis" },
                DeniedCapabilities: Array.Empty<string>());

            Assert.IsFalse(record.CanExecute(), license);
            AssertThrows<InvalidOperationException>(() => (record with { RuntimeAllowlisted = true }).Validate(), license);
        }
    }

    [TestMethod]
    public void ObservationRejectsAbbreviatedHeadRevision()
    {
        var observation = new RepositoryObservation(
            FullName: "example/runtime",
            CanonicalUrl: new Uri("https://github.com/example/runtime", UriKind.Absolute),
            DefaultBranch: "main",
            HeadRevision: "0123456",
            SpdxLicense: "MIT",
            Archived: false,
            ObservedAt: Now,
            ContentSha256: null);

        AssertThrows<ArgumentException>(() => observation.Validate());
    }

    private static void AssertThrows<TException>(Action action, string? context = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            Assert.Fail($"Expected {typeof(TException).Name} but observed {exception.GetType().Name}. {context}");
        }

        Assert.Fail($"Expected {typeof(TException).Name} but no exception was thrown. {context}");
    }

    private static RepositoryObservation Observation(
        string fullName,
        Uri canonicalUrl,
        string revision,
        string license,
        string? hash = null) => new(
            FullName: fullName,
            CanonicalUrl: canonicalUrl,
            DefaultBranch: "main",
            HeadRevision: revision,
            SpdxLicense: license,
            Archived: false,
            ObservedAt: Now,
            ContentSha256: hash);
}
