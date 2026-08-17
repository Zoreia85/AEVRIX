using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class TrustedKnowledgeBlueprintProjectionTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task ProjectAsync_TrustedConvergentKnowledgeBecomesReconstructableAndPreservesConservativeBasis()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-static", "static", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.ExperimentallyValidated, EvidenceSensitivity.Public, "framework", "ASP.NET", "ev-static");
        Publish(bus, "obs-dynamic", "dynamic", MissionSpecialistKind.DynamicAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.ProjectConfidential, "framework", "ASP.NET", "ev-dynamic");
        var knowledge = Knowledge(KnowledgeTrustState.Trusted, ["obs-static", "obs-dynamic"]);

        var requirement = await Projector(bus, knowledge).ProjectAsync(new MissionKnowledgeItem(
            "framework", EvidenceFusionState.Convergent, knowledge));

        Assert.AreEqual(BlueprintKnowledgePromotionLevel.Reconstructable, requirement.PromotionLevel);
        Assert.AreEqual(EvidenceObservationClass.Observed, requirement.Basis);
        Assert.AreEqual(EvidenceSensitivity.ProjectConfidential, requirement.Sensitivity);
        CollectionAssert.AreEquivalent(new[] { "obs-static", "obs-dynamic" }, requirement.EvidenceIds.ToArray());
        Assert.IsTrue(requirement.RequirementId.StartsWith("BKR-", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProjectAsync_ValidatedKnowledgeRemainsConditionalOnlyAfterIndependentConvergence()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-runtime-static", "runtime-static", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.ExperimentallyValidated, EvidenceSensitivity.Public, "runtime", ".NET", "ev-runtime-a");
        Publish(bus, "obs-runtime-dynamic", "runtime-dynamic", MissionSpecialistKind.DynamicAnalysis,
            EvidenceObservationClass.ExperimentallyValidated, EvidenceSensitivity.Public, "runtime", ".NET", "ev-runtime-b");
        var knowledge = Knowledge(
            KnowledgeTrustState.Validated,
            ["obs-runtime-static", "obs-runtime-dynamic"],
            statement: "runtime = .NET");

        var requirement = await Projector(bus, knowledge).ProjectAsync(new MissionKnowledgeItem(
            "runtime", EvidenceFusionState.Convergent, knowledge));

        Assert.AreEqual(BlueprintKnowledgePromotionLevel.Conditional, requirement.PromotionLevel);
        Assert.AreEqual(EvidenceObservationClass.ExperimentallyValidated, requirement.Basis);
    }

    [TestMethod]
    public async Task ProjectAsync_RejectsCandidateOrRejectedKnowledgeFromAuthoritativeRepository()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-candidate", "inspect", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "language", "C#", "ev-language");

        var candidate = Knowledge(KnowledgeTrustState.Candidate, ["obs-candidate"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Projector(bus, candidate).ProjectAsync(
            new MissionKnowledgeItem("language", EvidenceFusionState.Convergent, candidate)));

        var rejected = Knowledge(KnowledgeTrustState.Rejected, ["obs-candidate"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Projector(bus, rejected).ProjectAsync(
            new MissionKnowledgeItem("language", EvidenceFusionState.Convergent, rejected)));
    }

    [TestMethod]
    public async Task ProjectAsync_RejectsContestedAndInsufficientFusionAfterIndependentRecalculation()
    {
        var contestedBus = new EvidenceBus();
        Publish(contestedBus, "obs-dotnet", "inspect-dotnet", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "runtime", ".NET", "ev-dotnet");
        Publish(contestedBus, "obs-jvm", "inspect-jvm", MissionSpecialistKind.DynamicAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "runtime", "JVM", "ev-jvm");
        var contestedKnowledge = Knowledge(KnowledgeTrustState.Trusted, ["obs-dotnet", "obs-jvm"], statement: "runtime contested");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Projector(contestedBus, contestedKnowledge).ProjectAsync(
            new MissionKnowledgeItem("runtime", EvidenceFusionState.Contested, contestedKnowledge)));

        var insufficientBus = new EvidenceBus();
        Publish(insufficientBus, "obs-single", "inspect-single", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "runtime", ".NET", "ev-single");
        var insufficientKnowledge = Knowledge(KnowledgeTrustState.Trusted, ["obs-single"], statement: "runtime = .NET");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Projector(insufficientBus, insufficientKnowledge).ProjectAsync(
            new MissionKnowledgeItem("runtime", EvidenceFusionState.Insufficient, insufficientKnowledge)));
    }

    [TestMethod]
    public async Task ProjectAsync_RejectsForgedFusionState()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-only", "inspect-only", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "framework", "ASP.NET", "ev-only");
        var knowledge = Knowledge(KnowledgeTrustState.Trusted, ["obs-only"]);

        await Assert.ThrowsAsync<InvalidDataException>(() => Projector(bus, knowledge).ProjectAsync(
            new MissionKnowledgeItem("framework", EvidenceFusionState.Convergent, knowledge)));
    }

    [TestMethod]
    public async Task ProjectAsync_RejectsSuppliedIdentityThatDiffersFromAuthoritativeKnowledge()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-a", "inspect-a", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "framework", "ASP.NET", "ev-a");
        Publish(bus, "obs-b", "inspect-b", MissionSpecialistKind.DynamicAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "framework", "ASP.NET", "ev-b");
        var authoritative = Knowledge(KnowledgeTrustState.Trusted, ["obs-a", "obs-b"]);
        var supplied = authoritative with { ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222") };

        await Assert.ThrowsAsync<InvalidDataException>(() => Projector(bus, authoritative).ProjectAsync(
            new MissionKnowledgeItem("framework", EvidenceFusionState.Convergent, supplied)));
    }

    [TestMethod]
    public async Task ProjectAsync_FailsClosedWhenKnowledgeEvidenceIsMissingOrBoundToDifferentClaim()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-framework", "inspect", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "framework", "ASP.NET", "ev-framework");

        var missing = Knowledge(KnowledgeTrustState.Trusted, ["obs-missing"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Projector(bus, missing).ProjectAsync(
            new MissionKnowledgeItem("framework", EvidenceFusionState.Convergent, missing)));

        var wrongClaim = Knowledge(KnowledgeTrustState.Trusted, ["obs-framework"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Projector(bus, wrongClaim).ProjectAsync(
            new MissionKnowledgeItem("runtime", EvidenceFusionState.Convergent, wrongClaim)));
    }

    private static TrustedKnowledgeBlueprintProjector Projector(EvidenceBus bus, CandidateKnowledge authoritative) =>
        new(bus, new StaticRepository(authoritative));

    private static CandidateKnowledge Knowledge(
        KnowledgeTrustState state,
        IReadOnlyList<string> evidenceIds,
        string statement = "framework = ASP.NET") =>
        new(
            KnowledgeId: "KN-0123456789abcdef0123456789abcdef",
            ProjectId: ProjectId,
            TargetId: "target-001",
            Statement: statement,
            TrustState: state,
            Confidence: 0.96,
            Risk: ModelRiskLevel.Low,
            EvidenceIds: evidenceIds,
            ProviderTrace: ["evidence-fusion@fusion-v1:0.960:Low"],
            Assumptions: [],
            OpenQuestions: [],
            CreatedAt: new DateTimeOffset(2026, 8, 14, 21, 30, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 8, 14, 21, 31, 0, TimeSpan.Zero),
            ValidationRecordId: state is KnowledgeTrustState.Validated or KnowledgeTrustState.Trusted
                ? "VR-0123456789abcdef"
                : null);

    private static void Publish(
        EvidenceBus bus,
        string observationId,
        string taskId,
        MissionSpecialistKind specialist,
        EvidenceObservationClass observationClass,
        EvidenceSensitivity sensitivity,
        string claimKey,
        string claimValue,
        string parentEvidenceId)
    {
        var task = new MissionTaskSpec(taskId, specialist, "Collect governed evidence.", [parentEvidenceId], []);
        var context = new SpecialistExecutionContext(
            "mission-001", ProjectId, "target-001", task,
            new Dictionary<string, SpecialistTaskResult>());
        bus.PublishFromSpecialist(context, new EvidenceObservation(
            observationId,
            ProjectId,
            "target-001",
            taskId,
            specialist,
            observationClass,
            sensitivity,
            claimKey,
            claimValue,
            $"Observed {claimKey} as {claimValue}.",
            0.96,
            new string('a', 64),
            new DateTimeOffset(2026, 8, 14, 21, 29, 0, TimeSpan.Zero),
            [$"artifact-{taskId}"],
            [parentEvidenceId]));
    }

    private sealed class StaticRepository(CandidateKnowledge item) : ICandidateKnowledgeRepository
    {
        public Task StoreCandidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CandidateKnowledge?> LoadAsync(string knowledgeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(knowledgeId, item.KnowledgeId, StringComparison.Ordinal) ? item : null);

        public Task StoreValidationAsync(KnowledgeValidationRecord validation, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApplyValidationOutcomeAsync(
            string knowledgeId,
            KnowledgeTrustState state,
            string validationRecordId,
            DateTimeOffset decidedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PromoteTrustedAsync(
            TrustedKnowledgeAdmissionAuthorization authorization,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
