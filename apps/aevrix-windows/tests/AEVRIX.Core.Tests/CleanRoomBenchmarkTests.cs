using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class CleanRoomBenchmarkTests
{
    private static readonly string Sha = new('a', 64);

    [TestMethod]
    public void Evaluate_PassesForIndependentHighFidelityImplementation()
    {
        var evidence = new[]
        {
            Evidence("EV-001", CleanRoomEvidenceKind.PublicDocumentation),
            Evidence("EV-002", CleanRoomEvidenceKind.AuthorizedRuntimeObservation)
        };

        var requirements = new[]
        {
            Requirement("REQ-001", "EV-001"),
            Requirement("REQ-002", "EV-002")
        };

        var attestation = new CleanRoomImplementationAttestation(
            "IMP-001",
            "independent-team",
            ["REQ-001", "REQ-002"],
            ["AEVRIX-authored-code", "public-protocol-documentation"],
            HadDirectAccessToRestrictedImplementationArtifacts: false);

        var metrics = new[]
        {
            new CleanRoomMetricResult("behavior", 0.50, 0.96),
            new CleanRoomMetricResult("interoperability", 0.20, 0.95),
            new CleanRoomMetricResult("performance", 0.15, 0.90),
            new CleanRoomMetricResult("accessibility", 0.15, 0.92)
        };

        var report = CleanRoomBenchmarkProtocol.Evaluate(
            evidence,
            requirements,
            attestation,
            metrics,
            failedRequirements: [],
            passedIdentitySeparation: true,
            passedRestrictedArtifactGuard: true);

        Assert.AreEqual(0.9425, report.FunctionalEquivalence, 0.000001);
        Assert.IsTrue(report.Passed);
    }

    [TestMethod]
    public void Requirement_RejectsCopyingProtectedExpression()
    {
        var requirement = new CleanRoomRequirement(
            "REQ-001",
            CleanRoomRequirementClass.Functional,
            "Provide equivalent sheet nesting behavior.",
            ["EV-001"],
            MustMatchBehavior: true,
            MustNotCopyExpression: false);

        Assert.Throws<InvalidOperationException>(requirement.Validate);
    }

    [TestMethod]
    public void Attestation_RejectsRestrictedImplementationAccess()
    {
        var attestation = new CleanRoomImplementationAttestation(
            "IMP-001",
            "team",
            ["REQ-001"],
            ["restricted-source"],
            HadDirectAccessToRestrictedImplementationArtifacts: true);

        Assert.Throws<InvalidOperationException>(attestation.Validate);
    }

    [TestMethod]
    public void Evaluate_RejectsRequirementWithUnknownEvidence()
    {
        var attestation = ValidAttestation();

        Assert.Throws<InvalidOperationException>(() => CleanRoomBenchmarkProtocol.Evaluate(
            [Evidence("EV-001", CleanRoomEvidenceKind.PublicDocumentation)],
            [Requirement("REQ-001", "EV-MISSING")],
            attestation,
            ValidMetrics(),
            [],
            true,
            true));
    }

    [TestMethod]
    public void Evaluate_RejectsWeightsThatDoNotSumToOne()
    {
        var badMetrics = new[]
        {
            new CleanRoomMetricResult("behavior", 0.80, 1.0),
            new CleanRoomMetricResult("performance", 0.10, 1.0)
        };

        Assert.Throws<InvalidOperationException>(() => CleanRoomBenchmarkProtocol.Evaluate(
            [Evidence("EV-001", CleanRoomEvidenceKind.PublicDocumentation)],
            [Requirement("REQ-001", "EV-001")],
            ValidAttestation(),
            badMetrics,
            [],
            true,
            true));
    }

    [TestMethod]
    public void Evaluate_FailsGateWhenIdentitySeparationFails()
    {
        var report = CleanRoomBenchmarkProtocol.Evaluate(
            [Evidence("EV-001", CleanRoomEvidenceKind.PublicDocumentation)],
            [Requirement("REQ-001", "EV-001")],
            ValidAttestation(),
            ValidMetrics(),
            [],
            passedIdentitySeparation: false,
            passedRestrictedArtifactGuard: true);

        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void Evidence_RequiresCryptographicDigest()
    {
        var evidence = new CleanRoomEvidence(
            "EV-001",
            CleanRoomEvidenceKind.PublicDocumentation,
            new Uri("https://example.test/docs"),
            "Observed public behavior.",
            DateTimeOffset.UtcNow,
            "not-a-sha");

        Assert.Throws<ArgumentException>(evidence.Validate);
    }

    private static CleanRoomEvidence Evidence(string id, CleanRoomEvidenceKind kind) => new(
        id,
        kind,
        new Uri($"https://example.test/{id}"),
        $"Observation {id}",
        new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero),
        Sha);

    private static CleanRoomRequirement Requirement(string id, string evidenceId) => new(
        id,
        CleanRoomRequirementClass.Functional,
        $"Derived requirement {id}",
        [evidenceId],
        MustMatchBehavior: true,
        MustNotCopyExpression: true);

    private static CleanRoomImplementationAttestation ValidAttestation() => new(
        "IMP-001",
        "independent-team",
        ["REQ-001"],
        ["AEVRIX-authored-code"],
        HadDirectAccessToRestrictedImplementationArtifacts: false);

    private static CleanRoomMetricResult[] ValidMetrics() =>
    [
        new("behavior", 0.70, 0.95),
        new("interoperability", 0.30, 0.95)
    ];
}
