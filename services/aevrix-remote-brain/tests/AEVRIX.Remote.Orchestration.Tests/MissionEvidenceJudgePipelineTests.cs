using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class MissionEvidenceJudgePipelineTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task ExecuteAsync_PromotesOnlyConvergentValidatedEvidenceWithAnchoredProofClosure()
    {
        var bus = new EvidenceBus();
        var director = TestProofBoundMissionDirector.Create([
            new PublishingSpecialist(MissionSpecialistKind.StaticAnalysis, bus, "obs-static", "framework", "ASP.NET"),
            new PublishingSpecialist(MissionSpecialistKind.DynamicAnalysis, bus, "obs-dynamic", "framework", "ASP.NET")
        ]);
        var repository = new MemoryRepository();
        var plan = Plan([
            Spec("static", MissionSpecialistKind.StaticAnalysis, "ev-static"),
            Spec("dynamic", MissionSpecialistKind.DynamicAnalysis, "ev-dynamic")
        ]);
        var ledger = BuildLedger(plan, [
            ("static", MissionSpecialistKind.StaticAnalysis),
            ("dynamic", MissionSpecialistKind.DynamicAnalysis)
        ]);
        var pipeline = new MissionEvidenceJudgePipeline(
            director,
            bus,
            new EvidenceFusionEngine(),
            repository,
            new PassingValidator(),
            new StaticHeadAnchor(ledger.Head));

        var result = await pipeline.ExecuteAsync(new MissionKnowledgeRequest(
            plan,
            ["framework"],
            ProofRecords: ledger.Snapshot(),
            ExpectedProofHead: ledger.Head));

        Assert.IsTrue(result.Mission.RequiredTasksSucceeded);
        var item = result.KnowledgeItems.Single();
        Assert.AreEqual(EvidenceFusionState.Convergent, item.FusionState);
        Assert.AreEqual(KnowledgeTrustState.Trusted, item.Knowledge.TrustState);
        CollectionAssert.AreEquivalent(new[] { "obs-static", "obs-dynamic" }, item.Knowledge.EvidenceIds.ToArray());
        Assert.AreEqual(1, repository.TrustedPromotions);
    }

    [TestMethod]
    public async Task ExecuteAsync_ConvergentTrustedValidationWithoutProofAuthorityFailsClosed()
    {
        var bus = new EvidenceBus();
        var director = TestProofBoundMissionDirector.Create([
            new PublishingSpecialist(MissionSpecialistKind.StaticAnalysis, bus, "obs-static", "framework", "ASP.NET"),
            new PublishingSpecialist(MissionSpecialistKind.DynamicAnalysis, bus, "obs-dynamic", "framework", "ASP.NET")
        ]);
        var repository = new MemoryRepository();
        var pipeline = new MissionEvidenceJudgePipeline(
            director, bus, new EvidenceFusionEngine(), repository, new PassingValidator());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync(
            new MissionKnowledgeRequest(
                Plan([
                    Spec("static", MissionSpecialistKind.StaticAnalysis, "ev-static"),
                    Spec("dynamic", MissionSpecialistKind.DynamicAnalysis, "ev-dynamic")
                ]),
                ["framework"])));
    }

    [TestMethod]
    public async Task ExecuteAsync_LeavesContestedEvidenceAsCandidate()
    {
        var bus = new EvidenceBus();
        var director = TestProofBoundMissionDirector.Create([
            new PublishingSpecialist(MissionSpecialistKind.StaticAnalysis, bus, "obs-a", "runtime", ".NET"),
            new PublishingSpecialist(MissionSpecialistKind.DynamicAnalysis, bus, "obs-b", "runtime", "JVM")
        ]);
        var repository = new MemoryRepository();
        var pipeline = new MissionEvidenceJudgePipeline(
            director, bus, new EvidenceFusionEngine(), repository, new PassingValidator());

        var result = await pipeline.ExecuteAsync(new MissionKnowledgeRequest(
            Plan([
                Spec("static", MissionSpecialistKind.StaticAnalysis, "ev-static"),
                Spec("dynamic", MissionSpecialistKind.DynamicAnalysis, "ev-dynamic")
            ]),
            ["runtime"]));

        var item = result.KnowledgeItems.Single();
        Assert.AreEqual(EvidenceFusionState.Contested, item.FusionState);
        Assert.AreEqual(KnowledgeTrustState.Candidate, item.Knowledge.TrustState);
    }

    [TestMethod]
    public async Task ExecuteAsync_DoesNotCreateKnowledgeAfterRequiredMissionFailure()
    {
        var bus = new EvidenceBus();
        var director = TestProofBoundMissionDirector.Create([
            new FailingSpecialist(MissionSpecialistKind.StaticAnalysis)
        ]);
        var repository = new MemoryRepository();
        var pipeline = new MissionEvidenceJudgePipeline(
            director, bus, new EvidenceFusionEngine(), repository, new PassingValidator());

        var result = await pipeline.ExecuteAsync(new MissionKnowledgeRequest(
            Plan([Spec("static", MissionSpecialistKind.StaticAnalysis, "ev-static")]),
            ["framework"]));

        Assert.IsFalse(result.Mission.RequiredTasksSucceeded);
        Assert.AreEqual(0, result.KnowledgeItems.Count);
        Assert.AreEqual(0, repository.CandidateCount);
    }

    private static MissionPlan Plan(IReadOnlyList<MissionTaskSpec> tasks) => new(
        "mission-pipeline-001", ProjectId, "target-001", tasks, 4);

    private static MissionTaskSpec Spec(string id, MissionSpecialistKind kind, string evidenceId) =>
        new(id, kind, $"Analyze {id}.", [evidenceId], []);

    private static ExecutionProofLedger BuildLedger(
        MissionPlan plan,
        IReadOnlyList<(string TaskId, MissionSpecialistKind Specialist)> tasks)
    {
        var ledger = new ExecutionProofLedger();
        foreach (var item in tasks)
        {
            var executionId = MissionExecutionProofIdentity.CreateExecutionId(
                plan.ProjectId, plan.MissionId, plan.TargetId, item.TaskId, item.Specialist);
            var inputDigest = new string('a', 64);
            var resultDigest = item.Specialist == MissionSpecialistKind.StaticAnalysis ? new string('b', 64) : new string('c', 64);
            var at = DateTimeOffset.Parse("2026-08-17T12:10:00Z");
            ledger.Append(new ExecutionProofEvent(
                $"evt-start-{item.TaskId}", plan.ProjectId, plan.MissionId, executionId,
                ExecutionProofStage.Started, "mission-specialist", item.Specialist.ToString(),
                ExecutionProofOutcome.Pending, inputDigest, null, null, null, null, null, null, null, null, at));
            ledger.Append(new ExecutionProofEvent(
                $"evt-done-{item.TaskId}", plan.ProjectId, plan.MissionId, executionId,
                ExecutionProofStage.Completed, "mission-specialist", item.Specialist.ToString(),
                ExecutionProofOutcome.Succeeded, inputDigest, null, resultDigest, null, null, null, null, null, null,
                at.AddSeconds(1)));
        }
        return ledger;
    }

    private sealed class PublishingSpecialist(
        MissionSpecialistKind kind,
        EvidenceBus bus,
        string observationId,
        string claimKey,
        string claimValue) : IMissionSpecialist
    {
        public MissionSpecialistKind Kind => kind;

        public Task<SpecialistExecutionOutput> ExecuteAsync(SpecialistExecutionContext context, CancellationToken cancellationToken = default)
        {
            var parent = context.Task.EvidenceIds.Single();
            bus.PublishFromSpecialist(context, new EvidenceObservation(
                observationId,
                context.ProjectId,
                context.TargetId,
                context.Task.TaskId,
                context.Task.Specialist,
                EvidenceObservationClass.Observed,
                EvidenceSensitivity.Public,
                claimKey,
                claimValue,
                $"Observed {claimKey} as {claimValue}.",
                0.96,
                new string('a', 64),
                new DateTimeOffset(2026, 8, 14, 20, 0, 0, TimeSpan.Zero),
                [$"artifact-{context.Task.TaskId}"],
                [parent]));
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

    private sealed class StaticHeadAnchor(ExecutionProofHead head) : IExecutionProofHeadAnchor
    {
        public Task<ExecutionProofHead?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExecutionProofHead?>(projectId == ProjectId ? head : null);
        public Task AdvanceAsync(Guid projectId, ExecutionProofHead expectedPrevious, ExecutionProofHead next, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryRepository : ICandidateKnowledgeRepository
    {
        private readonly Dictionary<string, CandidateKnowledge> _items = new(StringComparer.Ordinal);
        public int CandidateCount => _items.Count;
        public int TrustedPromotions { get; private set; }

        public Task StoreCandidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default)
        {
            _items[candidate.KnowledgeId] = candidate;
            return Task.CompletedTask;
        }

        public Task<CandidateKnowledge?> LoadAsync(string knowledgeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.TryGetValue(knowledgeId, out var item) ? item : null);

        public Task StoreValidationAsync(KnowledgeValidationRecord validation, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApplyValidationOutcomeAsync(string knowledgeId, KnowledgeTrustState state, string validationRecordId, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        {
            if (state is KnowledgeTrustState.Candidate or KnowledgeTrustState.Trusted) throw new InvalidOperationException();
            var item = _items[knowledgeId];
            _items[knowledgeId] = item with { TrustState = state, ValidationRecordId = validationRecordId, UpdatedAt = decidedAt };
            return Task.CompletedTask;
        }

        public Task PromoteTrustedAsync(TrustedKnowledgeAdmissionAuthorization authorization, CancellationToken cancellationToken = default)
        {
            var item = _items[authorization.KnowledgeId];
            _items[authorization.KnowledgeId] = item with
            {
                TrustState = KnowledgeTrustState.Trusted,
                ValidationRecordId = authorization.ValidationRecordId,
                UpdatedAt = authorization.AdmittedAt
            };
            TrustedPromotions++;
            return Task.CompletedTask;
        }
    }
}
