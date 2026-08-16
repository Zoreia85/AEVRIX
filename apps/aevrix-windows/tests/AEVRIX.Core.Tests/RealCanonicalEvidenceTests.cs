using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class RealCanonicalEvidenceTests
{
    [TestMethod]
    public void CanonicalRepositoryFiles_AreHashedAndAcceptedAsRealCleanRoomInputs()
    {
        var root = FindRepositoryRoot();
        var fixtures = new[]
        {
            (Path: "README.md", Source: new Uri("https://github.com/Zoreia85/AEVRIX/blob/main/README.md")),
            (Path: "LICENSE", Source: new Uri("https://github.com/Zoreia85/AEVRIX/blob/main/LICENSE")),
            (Path: "docs/VALIDATION.md", Source: new Uri("https://github.com/Zoreia85/AEVRIX/blob/main/docs/VALIDATION.md"))
        };

        var evidence = fixtures.Select((fixture, index) =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, fixture.Path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.IsTrue(bytes.Length > 0, $"Canonical fixture {fixture.Path} is empty.");
            return new CleanRoomEvidence(
                $"EV-CANONICAL-FILE-{index + 1}",
                CleanRoomEvidenceKind.OpenSourceReference,
                fixture.Source,
                $"Canonical public AEVRIX file {fixture.Path}; {bytes.Length} bytes observed from the exact checkout under test.",
                DateTimeOffset.UtcNow,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }).ToArray();

        var requirements = evidence.Select((item, index) => new CleanRoomRequirement(
            $"REQ-CANONICAL-FILE-{index + 1}",
            CleanRoomRequirementClass.Reliability,
            "Preserve cryptographic provenance for a real canonical source artifact.",
            [item.Id],
            MustMatchBehavior: true,
            MustNotCopyExpression: true)).ToArray();

        var report = CleanRoomBenchmarkProtocol.Evaluate(
            evidence,
            requirements,
            new CleanRoomImplementationAttestation(
                "IMP-CANONICAL-FILE-PROVENANCE",
                "aevrix-validation",
                requirements.Select(static item => item.Id).ToArray(),
                ["AEVRIX-authored-code", "canonical-public-files"],
                HadDirectAccessToRestrictedImplementationArtifacts: false),
            [
                new CleanRoomMetricResult("artifact-integrity", 0.60, 1.0),
                new CleanRoomMetricResult("provenance-binding", 0.40, 1.0)
            ],
            failedRequirements: [],
            passedIdentitySeparation: true,
            passedRestrictedArtifactGuard: true);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(1.0, report.FunctionalEquivalence, 0.000001);
        Assert.IsTrue(evidence.All(static item => item.Sha256.Length == 64));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 16; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md"))
                && File.Exists(Path.Combine(current.FullName, "docs", "VALIDATION.md")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Unable to locate the canonical AEVRIX checkout for real-file validation.");
    }
}
