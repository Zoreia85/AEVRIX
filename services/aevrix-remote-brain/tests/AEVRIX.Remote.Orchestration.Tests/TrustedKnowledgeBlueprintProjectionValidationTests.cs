using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class TrustedKnowledgeBlueprintProjectionValidationTests
{
    [TestMethod]
    public void Project_RejectsMalformedKnowledgeBeforeEvidenceLookup()
    {
        var knowledge = new CandidateKnowledge(
            KnowledgeId: "KN-valid-shape",
            ProjectId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TargetId: "target-001",
            Statement: "runtime = .NET",
            TrustState: KnowledgeTrustState.Trusted,
            Confidence: 0.96,
            Risk: ModelRiskLevel.Low,
            EvidenceIds: [],
            ProviderTrace: [],
            Assumptions: [],
            OpenQuestions: [],
            CreatedAt: new DateTimeOffset(2026, 8, 14, 21, 30, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 8, 14, 21, 31, 0, TimeSpan.Zero),
            ValidationRecordId: "VR-valid-shape");

        Assert.Throws<InvalidDataException>(() => new TrustedKnowledgeBlueprintProjector(new EvidenceBus()).Project(
            new MissionKnowledgeItem("runtime", EvidenceFusionState.Convergent, knowledge)));
    }
}
