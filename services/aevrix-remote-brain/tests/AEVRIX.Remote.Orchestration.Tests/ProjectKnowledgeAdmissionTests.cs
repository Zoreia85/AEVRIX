namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class ProjectKnowledgeAdmissionTests
{
    private static readonly Guid ProjectId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string MissionId = "mission-memory-admission-001";
    private const string TargetId = "target-memory-001";

    [TestMethod]
    public async Task TrustedValidationWithoutExecutionProofContextFailsClosed()
    {
        var repository = new MemoryRepository();
        var judge = CreateJudge(repository, ["obs-static", "obs-dynamic"]);
        var candidate = await judge.AnalyzeToCandidateAsync(TaskFixture());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await judge.ValidateAndPromoteAsync(candidate.KnowledgeId));
        Assert.AreEqual(KnowledgeTrustState.Candidate, (await repository.LoadAsync(candidate.KnowledgeId))!.TrustState);
    }

    [TestMethod]
    public async Task TrustedAdmissionRequiresExactSuccessfulExecutionProvenance()
    {
        var observations = Observations();
        var ledger = BuildLedger(observations);
        var repository = new MemoryRepository();
        var judge = CreateJudge(repository, observations.Select(static item => item.EvidenceId).ToArray());
        var candidate = await judge.AnalyzeToCandidateAsync(TaskFixture());
        var trusted = await judge.ValidateAndPromoteAsync(
            candidate.KnowledgeId,
            new MemoryAdmissionContext(MissionId, observations, ledger.Snapshot(), ledger.Head));
        Assert.AreEqual(KnowledgeTrustState.Trusted, trusted.TrustState);
        Assert.IsFalse(string.IsNullOrWhiteSpace(trusted.ValidationRecordId));
    }

    [TestMethod]
    public async Task PartialValidatedEvidenceSetCannotEnterTrustedMemory()
    {
        var observations = Observations();
        var ledger = BuildLedger(observations);
        var repository = new MemoryRepository();
        var provider = new StubProvider(observations.Select(static item => item.EvidenceId).ToArray());
        var judge = new OrchestratorJudge(
            provider,
            repository,
            new StubValidator(candidate => TrustedValidation(candidate) with { ValidatedEvidenceIds = ["obs-static"] }),
            timeProvider: new FixedTimeProvider());
        var candidate = await judge.AnalyzeToCandidateAsync(TaskFixture());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await judge.ValidateAndPromoteAsync(
            candidate.KnowledgeId,
            new MemoryAdmissionContext(MissionId, observations, ledger.Snapshot(), ledger.Head)));
    }

    [TestMethod]
    public async Task PersonalDataObservationCannotEnterTrustedMemory()
    {
        var observations = Observations();
        observations[0] = observations[0] with { Sensitivity = EvidenceSensitivity.PersonalData, ContainsPersonalData = true };
        var ledger = BuildLedger(observations);
        var repository = new MemoryRepository();
        var judge = CreateJudge(repository, observations.Select(static item => item.EvidenceId).ToArray());
        var candidate = await judge.AnalyzeToCandidateAsync(TaskFixture());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await judge.ValidateAndPromoteAsync(
            candidate.KnowledgeId,
            new MemoryAdmissionContext(MissionId, observations, ledger.Snapshot(), ledger.Head)));
    }

    [TestMethod]
    public async Task ForgedLedgerHeadCannotEnterTrustedMemory()
    {
        var observations = Observations();
        var ledger = BuildLedger(observations);
        var repository = new MemoryRepository();
        var judge = CreateJudge(repository, observations.Select(static item => item.EvidenceId).ToArray());
        var candidate = await judge.AnalyzeToCandidateAsync(TaskFixture());
        var forgedHead = ledger.Head with { HeadHashSha256 = new string('0', 64) };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await judge.ValidateAndPromoteAsync(
            candidate.KnowledgeId,
            new MemoryAdmissionContext(MissionId, observations, ledger.Snapshot(), forgedHead)));
    }

    private static OrchestratorJudge CreateJudge(MemoryRepository repository, IReadOnlyList<string> evidenceIds) =>
        new(new StubProvider(evidenceIds), repository, new StubValidator(TrustedValidation), timeProvider: new FixedTimeProvider());

    private static AnalysisTask TaskFixture() => new(
        "task-memory-admission-001", ProjectId, TargetId,
        "Determine the governed framework from admitted observations.",
        ["obs-static", "obs-dynamic"], new Dictionary<string, string>());

    private static EvidenceObservation[] Observations() =>
    [
        Observation("obs-static", "task-static", MissionSpecialistKind.StaticAnalysis, 'a'),
        Observation("obs-dynamic", "task-dynamic", MissionSpecialistKind.DynamicAnalysis, 'b')
    ];

    private static EvidenceObservation Observation(string evidenceId, string taskId, MissionSpecialistKind specialist, char digestChar) => new(
        evidenceId, ProjectId, TargetId, taskId, specialist,
        EvidenceObservationClass.Observed, EvidenceSensitivity.ProjectConfidential,
        "runtime.framework", "governed-runtime", "Sanitized governed observation.", 0.97,
        new string(digestChar, 64), DateTimeOffset.Parse("2026-08-17T12:00:00Z"),
        [$"artifact-{taskId}"], [$"parent-{evidenceId}"]);

    private static ExecutionProofLedger BuildLedger(IEnumerable<EvidenceObservation> observations)
    {
        var ledger = new ExecutionProofLedger();
        foreach (var observation in observations)
        {
            var executionId = MissionExecutionProofIdentity.CreateExecutionId(
                ProjectId, MissionId, TargetId, observation.SourceTaskId, observation.Specialist);
            var inputDigest = new string('c', 64);
            var resultDigest = observation.Specialist == MissionSpecialistKind.StaticAnalysis ? new string('d', 64) : new string('e', 64);
            var at = DateTimeOffset.Parse("2026-08-17T12:01:00Z");
            ledger.Append(new ExecutionProofEvent(
                $"evt-start-{observation.SourceTaskId}", ProjectId, MissionId, executionId,
                ExecutionProofStage.Started, "mission-specialist", observation.Specialist.ToString(),
                ExecutionProofOutcome.Pending, inputDigest, null, null, null, null, null, null, null, null, at));
            ledger.Append(new ExecutionProofEvent(
                $"evt-done-{observation.SourceTaskId}", ProjectId, MissionId, executionId,
                ExecutionProofStage.Completed, "mission-specialist", observation.Specialist.ToString(),
                ExecutionProofOutcome.Succeeded, inputDigest, null, resultDigest, null, null, null, null, null, null,
                at.AddSeconds(1)));
        }
        return ledger;
    }

    private static KnowledgeValidationRecord TrustedValidation(CandidateKnowledge candidate) => new(
        $"VAL-{candidate.KnowledgeId}", candidate.KnowledgeId, true, true, true, true,
        candidate.EvidenceIds, [], DateTimeOffset.Parse("2026-08-17T12:02:00Z"));

    private sealed class StubProvider(IReadOnlyList<string> evidenceIds) : IAevrixModelProvider
    {
        public string ProviderId => "memory-admission-fixture";
        public Task<ModelAnalysisCandidate> AnalyzeAsync(AnalysisTask task, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelAnalysisCandidate(
                ProviderId, "v1", "runtime.framework = governed-runtime", 0.98, ModelRiskLevel.Low, evidenceIds, [], []));
    }

    private sealed class StubValidator(Func<CandidateKnowledge, KnowledgeValidationRecord> factory) : IEvidenceValidationService
    {
        public Task<KnowledgeValidationRecord> ValidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default) =>
            Task.FromResult(factory(candidate));
    }

    private sealed class MemoryRepository : ICandidateKnowledgeRepository
    {
        private readonly Dictionary<string, CandidateKnowledge> _items = new(StringComparer.Ordinal);
        public Task StoreCandidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default)
        { _items[candidate.KnowledgeId] = candidate; return Task.CompletedTask; }
        public Task<CandidateKnowledge?> LoadAsync(string knowledgeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.TryGetValue(knowledgeId, out var item) ? item : null);
        public Task StoreValidationAsync(KnowledgeValidationRecord validation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PromoteAsync(string knowledgeId, KnowledgeTrustState state, string validationRecordId, DateTimeOffset promotedAt, CancellationToken cancellationToken = default)
        {
            var current = _items[knowledgeId];
            _items[knowledgeId] = current with { TrustState = state, ValidationRecordId = validationRecordId, UpdatedAt = promotedAt };
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-17T12:03:00Z"); }
}
