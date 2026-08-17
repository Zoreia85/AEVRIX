using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class OrchestratorJudgeTests
{
    [TestMethod]
    public async Task HighConfidencePrimaryStillPersistsOnlyCandidateKnowledge()
    {
        var task = TaskFixture();
        var primary = new StubProvider("primary", Candidate("Primary observation", 0.99, ModelRiskLevel.Low, ["EV-1"]));
        var repository = new MemoryRepository();
        var validator = new StubValidator(TrustedValidation("unused", "unused"));
        var judge = new OrchestratorJudge(primary, repository, validator, timeProvider: new FixedTimeProvider());

        var result = await judge.AnalyzeToCandidateAsync(task);

        Assert.AreEqual(KnowledgeTrustState.Candidate, result.TrustState);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(1, repository.Candidates.Count);
        Assert.AreEqual(0, repository.Promotions.Count, "Model output must never auto-promote into trusted memory.");
        CollectionAssert.AreEqual(new[] { "EV-1" }, result.EvidenceIds.ToArray());
    }

    [TestMethod]
    public async Task RiskyOrInsufficientPrimaryEscalatesButConsolidatedOutputRemainsCandidate()
    {
        var task = TaskFixture();
        var primary = new StubProvider("primary", Candidate("Hypothesis A", 0.80, ModelRiskLevel.Medium, ["EV-1"]));
        var secondary = new StubProvider("secondary", Candidate("Hypothesis B", 0.96, ModelRiskLevel.Low, ["EV-2"]));
        var repository = new MemoryRepository();
        var judge = new OrchestratorJudge(primary, repository, new StubValidator(TrustedValidation("unused", "unused")), secondary, timeProvider: new FixedTimeProvider());

        var result = await judge.AnalyzeToCandidateAsync(task);

        Assert.AreEqual(1, secondary.CallCount);
        Assert.AreEqual(KnowledgeTrustState.Candidate, result.TrustState);
        CollectionAssert.AreEquivalent(new[] { "EV-1", "EV-2" }, result.EvidenceIds.ToArray());
        Assert.IsTrue(result.Statement.Contains("Primary candidate:", StringComparison.Ordinal));
        Assert.AreEqual(0, repository.Promotions.Count);
    }

    [TestMethod]
    public async Task CandidateCannotBecomeTrustedWithoutIndependentValidationCounterexampleReviewAndAdmissionContext()
    {
        var task = TaskFixture();
        var repository = new MemoryRepository();
        var primary = new StubProvider("primary", Candidate("Evidence-backed statement", 0.98, ModelRiskLevel.Low, ["EV-1", "EV-2"]));
        var judgeCreate = new OrchestratorJudge(primary, repository, new StubValidator(TrustedValidation("unused", "unused")), timeProvider: new FixedTimeProvider());
        var candidate = await judgeCreate.AnalyzeToCandidateAsync(task);

        var partialValidation = new KnowledgeValidationRecord(
            "VAL-partial",
            candidate.KnowledgeId,
            EvidenceIntegrityPassed: true,
            EvidenceSupportsStatement: true,
            IndependentValidationPassed: false,
            CounterexampleReviewPassed: true,
            ["EV-1"],
            [],
            DateTimeOffset.Parse("2026-08-14T04:01:00Z"));
        var partialJudge = new OrchestratorJudge(primary, repository, new StubValidator(partialValidation), timeProvider: new FixedTimeProvider());
        var partial = await partialJudge.ValidateAndPromoteAsync(candidate.KnowledgeId);
        Assert.AreEqual(KnowledgeTrustState.Validated, partial.TrustState);
        Assert.AreEqual(KnowledgeTrustState.Validated, repository.Promotions.Last().State);

        var repository2 = new MemoryRepository();
        var judgeCreate2 = new OrchestratorJudge(primary, repository2, new StubValidator(TrustedValidation("unused", "unused")), timeProvider: new FixedTimeProvider());
        var candidate2 = await judgeCreate2.AnalyzeToCandidateAsync(task with { TaskId = "task-fixture-0002" });
        var trustedJudge = new OrchestratorJudge(
            primary,
            repository2,
            new StubValidator(TrustedValidation("VAL-trusted", candidate2.KnowledgeId)),
            timeProvider: new FixedTimeProvider());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await trustedJudge.ValidateAndPromoteAsync(candidate2.KnowledgeId));
        Assert.AreEqual(0, repository2.Promotions.Count);
    }

    [TestMethod]
    public async Task ProviderAndValidatorCannotIntroduceEvidenceOutsideGovernedTask()
    {
        var task = TaskFixture();
        var repository = new MemoryRepository();
        var badProvider = new StubProvider("bad", Candidate("Bad", 0.99, ModelRiskLevel.Low, ["EV-OUTSIDE"]));
        var judge = new OrchestratorJudge(badProvider, repository, new StubValidator(TrustedValidation("x", "x")), timeProvider: new FixedTimeProvider());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await judge.AnalyzeToCandidateAsync(task));

        var goodProvider = new StubProvider("good", Candidate("Good", 0.99, ModelRiskLevel.Low, ["EV-1"]));
        var createJudge = new OrchestratorJudge(goodProvider, repository, new StubValidator(TrustedValidation("x", "x")), timeProvider: new FixedTimeProvider());
        var candidate = await createJudge.AnalyzeToCandidateAsync(task with { TaskId = "task-fixture-0003" });
        var outsideValidation = TrustedValidation("VAL-outside", candidate.KnowledgeId) with { ValidatedEvidenceIds = ["EV-OUTSIDE"] };
        var validateJudge = new OrchestratorJudge(goodProvider, repository, new StubValidator(outsideValidation), timeProvider: new FixedTimeProvider());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await validateJudge.ValidateAndPromoteAsync(candidate.KnowledgeId));
    }

    private static AnalysisTask TaskFixture() => new(
        "task-fixture-0001",
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "fixture-target",
        "Determine the evidence-backed architecture behavior.",
        ["EV-1", "EV-2"],
        new Dictionary<string, string> { ["scope"] = "authorized-read-only" });

    private static ModelAnalysisCandidate Candidate(string statement, double confidence, ModelRiskLevel risk, IReadOnlyList<string> evidence) => new(
        "provider", "model-v1", statement, confidence, risk, evidence, [], []);

    private static KnowledgeValidationRecord TrustedValidation(string id, string knowledgeId) => new(
        id, knowledgeId, true, true, true, true, ["EV-1"], [], DateTimeOffset.Parse("2026-08-14T04:01:00Z"));

    private sealed class StubProvider(string id, ModelAnalysisCandidate candidate) : IAevrixModelProvider
    {
        public string ProviderId { get; } = id;
        public int CallCount { get; private set; }
        public Task<ModelAnalysisCandidate> AnalyzeAsync(AnalysisTask task, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(candidate with { ProviderId = ProviderId });
        }
    }

    private sealed class StubValidator(KnowledgeValidationRecord record) : IEvidenceValidationService
    {
        public Task<KnowledgeValidationRecord> ValidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(record);
        }
    }

    private sealed class MemoryRepository : ICandidateKnowledgeRepository
    {
        public Dictionary<string, CandidateKnowledge> Candidates { get; } = new(StringComparer.Ordinal);
        public List<(string Id, KnowledgeTrustState State)> Promotions { get; } = [];

        public Task StoreCandidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidates[candidate.KnowledgeId] = candidate;
            return Task.CompletedTask;
        }

        public Task<CandidateKnowledge?> LoadAsync(string knowledgeId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidates.TryGetValue(knowledgeId, out var value);
            return Task.FromResult(value);
        }

        public Task StoreValidationAsync(KnowledgeValidationRecord validation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PromoteAsync(string knowledgeId, KnowledgeTrustState state, string validationRecordId, DateTimeOffset promotedAt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Promotions.Add((knowledgeId, state));
            if (Candidates.TryGetValue(knowledgeId, out var value))
                Candidates[knowledgeId] = value with { TrustState = state, ValidationRecordId = validationRecordId, UpdatedAt = promotedAt };
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-14T04:00:00Z");
    }
}
