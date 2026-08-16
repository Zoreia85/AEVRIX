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

    [TestMethod]
    public void ExecutionProvenanceBinder_RejectsPersonalDataBeforeBlueprintBinding()
    {
        var project = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var requirement = new BlueprintKnowledgeRequirement(
            "BKR-pii-gate", project, "target-001", "runtime.framework", "Observed runtime.",
            EvidenceObservationClass.Observed, EvidenceSensitivity.ProjectConfidential,
            BlueprintKnowledgePromotionLevel.Reconstructable, 0.95, ["ev-pii"], "KN-pii", "VR-pii");
        var observation = new EvidenceObservation(
            "ev-pii", project, "target-001", "task-static", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.PersonalData,
            "runtime.framework", "dotnet", "Sanitized test observation.", 0.95, new string('a', 64),
            DateTimeOffset.Parse("2026-08-16T03:00:00Z"), ["artifact-ref"], ["parent-evidence"],
            ContainsPersonalData: true);

        Assert.Throws<InvalidOperationException>(() =>
            new BlueprintExecutionProvenanceBinder().Bind(
                requirement, "mission-alpha", [observation], [], ExecutionProofHead.Empty));
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
