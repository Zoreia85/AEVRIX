using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class MissionEvidenceJudgePipelineTests
{
    [TestMethod]
    public async Task ExecuteAsync_PromotesOnlyConvergentValidatedEvidence()
    {
        var bus = new EvidenceBus();
        var director = new MissionDirector([
            new PublishingSpecialist(MissionSpecialistKind.StaticAnalysis, bus, "obs-static", "framework", "ASP.NET"),
            new PublishingSpecialist(MissionSpecialistKind.DynamicAnalysis, bus, "obs-dynamic", "framework", "ASP.NET")
        ]);
        var repository = new MemoryRepository();
        var pipeline = new MissionEvidenceJudgePipeline(director, bus, new EvidenceFusionEngine(), repository, new PassingValidator());
        var result = await pipeline.ExecuteAsync(new MissionKnowledgeRequest(
            Plan([Spec("static", MissionSpecialistKind.StaticAnalysis, "ev-static"), Spec("dynamic", MissionSpecialistKind.DynamicAnalysis, "ev-dynamic")]),
            ["framework"]));

        var item = result.KnowledgeItems.Single();
        Assert.AreEqual(EvidenceFusionState.Convergent, item.FusionState);
        Assert.AreEqual(KnowledgeTrustState.Trusted, item.Knowledge.TrustState);
        CollectionAssert.AreEquivalent(new[] { "obs-static", "obs-dynamic" }, item.Knowledge.EvidenceIds.ToArray());
    }

    [TestMethod]
    public async Task ExecuteAsync_LeavesContestedEvidenceAsCandidate()
    {
        var bus = new EvidenceBus();
        var director = new MissionDirector([
            new PublishingSpecialist(MissionSpecialistKind.StaticAnalysis, bus, "obs-a", "runtime", ".NET"),
            new PublishingSpecialist(MissionSpecialistKind.DynamicAnalysis, bus, "obs-b", "runtime", "JVM")
        ]);
        var repository = new MemoryRepository();
        var pipeline = new MissionEvidenceJudgePipeline(director, bus, new EvidenceFusionEngine(), repository, new PassingValidator());
        var result = await pipeline.ExecuteAsync(new MissionKnowledgeRequest(
            Plan([Spec("static", MissionSpecialistKind.StaticAnalysis, "ev-static"), Spec("dynamic", MissionSpecialistKind.DynamicAnalysis, "ev-dynamic")]),
            ["runtime"]));

        var item = result.KnowledgeItems.Single();
        Assert.AreEqual(EvidenceFusionState.Contested, item.FusionState);
        Assert.AreEqual(KnowledgeTrustState.Candidate, item.Knowledge.TrustState);
    }

    [TestMethod]
    public async Task ExecuteAsync_DoesNotCreateKnowledgeAfterRequiredMissionFailure()
    {
        var bus = new EvidenceBus();
        var repository = new MemoryRepository();
        var pipeline = new MissionEvidenceJudgePipeline(
            new MissionDirector([new FailingSpecialist(MissionSpecialistKind.StaticAnalysis)]),
            bus, new EvidenceFusionEngine(), repository, new PassingValidator());
        var result = await pipeline.ExecuteAsync(new MissionKnowledgeRequest(
            Plan([Spec("static", MissionSpecialistKind.StaticAnalysis, "ev-static")]), ["framework"]));

        Assert.IsFalse(result.Mission.RequiredTasksSucceeded);
        Assert.AreEqual(0, result.KnowledgeItems.Count);
        Assert.AreEqual(0, repository.CandidateCount);
    }

    private static MissionPlan Plan(IReadOnlyList<MissionTaskSpec> tasks) => new(
        "mission-pipeline-001", Guid.Parse("11111111-1111-1111-1111-111111111111"), "target-001", tasks, 4);

    private static MissionTaskSpec Spec(string id, MissionSpecialistKind kind, string evidenceId) =>
        new(id, kind, $"Analyze {id}.", [evidenceId], []);

    private sealed class PublishingSpecialist(MissionSpecialistKind kind, EvidenceBus bus, string observationId, string claimKey, string claimValue) : IMissionSpecialist
    {
        public MissionSpecialistKind Kind => kind;
        public Task<SpecialistExecutionOutput> ExecuteAsync(SpecialistExecutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = context.Task.EvidenceIds.Single();
            bus.PublishFromSpecialist(context, new EvidenceObservation(
                observationId, context.ProjectId, context.TargetId, context.Task.TaskId, context.Task.Specialist,
                EvidenceObservationClass.Observed, EvidenceSensitivity.Public, claimKey, claimValue,
                $"Observed {claimKey} as {claimValue}.", 0.96, new string('a', 64),
                new DateTimeOffset(2026, 8, 14, 20, 0, 0, TimeSpan.Zero),
                [$"artifact-{context.Task.TaskId}"], [parent]));
            return Task.FromResult(new SpecialistExecutionOutput(
                $"Published {observationId}.", 0.96, context.Task.EvidenceIds, [$"artifact-{context.Task.TaskId}"]));
        }
    }

    private sealed class FailingSpecialist(MissionSpecialistKind kind) : IMissionSpecialist
    {
        public MissionSpecialistKind Kind => kind;
        public Task<SpecialistExecutionOutput> ExecuteAsync(SpecialistExecutionContext context, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated failure");
    }

    private sealed class PassingValidator : IEvidenceValidationService
    {
        public Task<KnowledgeValidationRecord> ValidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgeValidationRecord(
                $"VR-{candidate.KnowledgeId}", candidate.KnowledgeId, true, true, true, true,
                candidate.EvidenceIds, [], new DateTimeOffset(2026, 8, 14, 20, 1, 0, TimeSpan.Zero)));
    }

    private sealed class MemoryRepository : ICandidateKnowledgeRepository
    {
        private readonly Dictionary<string, CandidateKnowledge> _items = new(StringComparer.Ordinal);
        public int CandidateCount => _items.Count;
        public Task StoreCandidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default) { _items[candidate.KnowledgeId] = candidate; return Task.CompletedTask; }
        public Task<CandidateKnowledge?> LoadAsync(string knowledgeId, CancellationToken cancellationToken = default) => Task.FromResult(_items.TryGetValue(knowledgeId, out var item) ? item : null);
        public Task StoreValidationAsync(KnowledgeValidationRecord validation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PromoteAsync(string knowledgeId, KnowledgeTrustState state, string validationRecordId, DateTimeOffset promotedAt, CancellationToken cancellationToken = default)
        {
            var item = _items[knowledgeId];
            _items[knowledgeId] = item with { TrustState = state, ValidationRecordId = validationRecordId, UpdatedAt = promotedAt };
            return Task.CompletedTask;
        }
    }
}
