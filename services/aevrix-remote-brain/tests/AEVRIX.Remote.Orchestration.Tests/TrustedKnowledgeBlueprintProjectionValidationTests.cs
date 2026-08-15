using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class TrustedKnowledgeBlueprintProjectionValidationTests
{
    [TestMethod]
    public async Task ProjectAsync_RejectsMalformedAuthoritativeKnowledgeBeforeEvidenceLookup()
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

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new TrustedKnowledgeBlueprintProjector(new EvidenceBus(), new StaticRepository(knowledge)).ProjectAsync(
                new MissionKnowledgeItem("runtime", EvidenceFusionState.Convergent, knowledge)));
    }

    private sealed class StaticRepository(CandidateKnowledge item) : ICandidateKnowledgeRepository
    {
        public Task StoreCandidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CandidateKnowledge?> LoadAsync(string knowledgeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(knowledgeId, item.KnowledgeId, StringComparison.Ordinal) ? item : null);

        public Task StoreValidationAsync(KnowledgeValidationRecord validation, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PromoteAsync(
            string knowledgeId,
            KnowledgeTrustState state,
            string validationRecordId,
            DateTimeOffset promotedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
