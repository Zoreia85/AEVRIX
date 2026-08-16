using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class TrustedKnowledgeBlueprintPrivacyTests
{
    [TestMethod]
    public async Task ProjectAsync_RequiresPersonalDataSanitizationBeforeBlueprintPromotion()
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bus = new EvidenceBus();
        Publish(bus, projectId, "obs-pii-a", "pii-a", MissionSpecialistKind.StaticAnalysis, "ev-pii-a");
        Publish(bus, projectId, "obs-pii-b", "pii-b", MissionSpecialistKind.DynamicAnalysis, "ev-pii-b");

        var knowledge = new CandidateKnowledge(
            "KN-privacy-0123456789abcdef",
            projectId,
            "target-001",
            "user-field = customer-email",
            KnowledgeTrustState.Trusted,
            0.96,
            ModelRiskLevel.Low,
            ["obs-pii-a", "obs-pii-b"],
            ["evidence-fusion@fusion-v1:0.960:Low"],
            [],
            [],
            new DateTimeOffset(2026, 8, 14, 21, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 14, 21, 31, 0, TimeSpan.Zero),
            "VR-privacy-0123456789abcdef");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TrustedKnowledgeBlueprintProjector(bus, new StaticRepository(knowledge)).ProjectAsync(
                new MissionKnowledgeItem("user-field", EvidenceFusionState.Convergent, knowledge)));
    }

    private static void Publish(
        EvidenceBus bus,
        Guid projectId,
        string evidenceId,
        string taskId,
        MissionSpecialistKind specialist,
        string parentEvidenceId)
    {
        var task = new MissionTaskSpec(taskId, specialist, "Observe personal-data-bearing field.", [parentEvidenceId], []);
        var context = new SpecialistExecutionContext("mission-privacy", projectId, "target-001", task,
            new Dictionary<string, SpecialistTaskResult>());
        bus.PublishFromSpecialist(context, new EvidenceObservation(
            evidenceId,
            projectId,
            "target-001",
            taskId,
            specialist,
            EvidenceObservationClass.Observed,
            EvidenceSensitivity.PersonalData,
            "user-field",
            "customer-email",
            "Observed a field containing personal data; raw values are not included.",
            0.96,
            new string('b', 64),
            new DateTimeOffset(2026, 8, 14, 21, 29, 0, TimeSpan.Zero),
            [$"artifact-{taskId}"],
            [parentEvidenceId],
            ContainsPersonalData: true));
    }

    private sealed class StaticRepository(CandidateKnowledge item) : ICandidateKnowledgeRepository
    {
        public Task StoreCandidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CandidateKnowledge?> LoadAsync(string knowledgeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(knowledgeId, item.KnowledgeId, StringComparison.Ordinal) ? item : null);
        public Task StoreValidationAsync(KnowledgeValidationRecord validation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PromoteAsync(string knowledgeId, KnowledgeTrustState state, string validationRecordId, DateTimeOffset promotedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
