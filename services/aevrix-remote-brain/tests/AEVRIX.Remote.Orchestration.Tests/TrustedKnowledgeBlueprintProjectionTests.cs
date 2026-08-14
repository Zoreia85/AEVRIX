using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class TrustedKnowledgeBlueprintProjectionTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public void Project_TrustedConvergentKnowledgeBecomesReconstructableAndPreservesConservativeBasis()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-static", "static", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.ExperimentallyValidated, EvidenceSensitivity.Public, "framework", "ASP.NET", "ev-static");
        Publish(bus, "obs-dynamic", "dynamic", MissionSpecialistKind.DynamicAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.ProjectConfidential, "framework", "ASP.NET", "ev-dynamic");

        var requirement = new TrustedKnowledgeBlueprintProjector(bus).Project(new MissionKnowledgeItem(
            "framework",
            EvidenceFusionState.Convergent,
            Knowledge(KnowledgeTrustState.Trusted, ["obs-static", "obs-dynamic"])));

        Assert.AreEqual(BlueprintKnowledgePromotionLevel.Reconstructable, requirement.PromotionLevel);
        Assert.AreEqual(EvidenceObservationClass.Observed, requirement.Basis);
        Assert.AreEqual(EvidenceSensitivity.ProjectConfidential, requirement.Sensitivity);
        CollectionAssert.AreEquivalent(new[] { "obs-static", "obs-dynamic" }, requirement.EvidenceIds.ToArray());
        Assert.IsTrue(requirement.RequirementId.StartsWith("BKR-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Project_ValidatedKnowledgeRemainsConditionalOnlyAfterIndependentConvergence()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-runtime-static", "runtime-static", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.ExperimentallyValidated, EvidenceSensitivity.Public, "runtime", ".NET", "ev-runtime-a");
        Publish(bus, "obs-runtime-dynamic", "runtime-dynamic", MissionSpecialistKind.DynamicAnalysis,
            EvidenceObservationClass.ExperimentallyValidated, EvidenceSensitivity.Public, "runtime", ".NET", "ev-runtime-b");

        var requirement = new TrustedKnowledgeBlueprintProjector(bus).Project(new MissionKnowledgeItem(
            "runtime",
            EvidenceFusionState.Convergent,
            Knowledge(KnowledgeTrustState.Validated, ["obs-runtime-static", "obs-runtime-dynamic"], statement: "runtime = .NET")));

        Assert.AreEqual(BlueprintKnowledgePromotionLevel.Conditional, requirement.PromotionLevel);
        Assert.AreEqual(EvidenceObservationClass.ExperimentallyValidated, requirement.Basis);
    }

    [TestMethod]
    public void Project_RejectsCandidateOrRejectedKnowledge()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-candidate", "inspect", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "language", "C#", "ev-language");
        var projector = new TrustedKnowledgeBlueprintProjector(bus);

        Assert.Throws<InvalidOperationException>(() => projector.Project(new MissionKnowledgeItem(
            "language", EvidenceFusionState.Convergent, Knowledge(KnowledgeTrustState.Candidate, ["obs-candidate"]))));
        Assert.Throws<InvalidOperationException>(() => projector.Project(new MissionKnowledgeItem(
            "language", EvidenceFusionState.Convergent, Knowledge(KnowledgeTrustState.Rejected, ["obs-candidate"]))));
    }

    [TestMethod]
    public void Project_RejectsContestedAndInsufficientFusionAfterIndependentRecalculation()
    {
        var contestedBus = new EvidenceBus();
        Publish(contestedBus, "obs-dotnet", "inspect-dotnet", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "runtime", ".NET", "ev-dotnet");
        Publish(contestedBus, "obs-jvm", "inspect-jvm", MissionSpecialistKind.DynamicAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "runtime", "JVM", "ev-jvm");
        var contestedKnowledge = Knowledge(KnowledgeTrustState.Trusted, ["obs-dotnet", "obs-jvm"], statement: "runtime contested");

        Assert.Throws<InvalidOperationException>(() => new TrustedKnowledgeBlueprintProjector(contestedBus).Project(
            new MissionKnowledgeItem("runtime", EvidenceFusionState.Contested, contestedKnowledge)));

        var insufficientBus = new EvidenceBus();
        Publish(insufficientBus, "obs-single", "inspect-single", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "runtime", ".NET", "ev-single");
        var insufficientKnowledge = Knowledge(KnowledgeTrustState.Trusted, ["obs-single"], statement: "runtime = .NET");

        Assert.Throws<InvalidOperationException>(() => new TrustedKnowledgeBlueprintProjector(insufficientBus).Project(
            new MissionKnowledgeItem("runtime", EvidenceFusionState.Insufficient, insufficientKnowledge)));
    }

    [TestMethod]
    public void Project_RejectsForgedFusionState()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-only", "inspect-only", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "framework", "ASP.NET", "ev-only");

        Assert.Throws<InvalidDataException>(() => new TrustedKnowledgeBlueprintProjector(bus).Project(
            new MissionKnowledgeItem(
                "framework",
                EvidenceFusionState.Convergent,
                Knowledge(KnowledgeTrustState.Trusted, ["obs-only"]))));
    }

    [TestMethod]
    public void Project_FailsClosedWhenKnowledgeEvidenceIsMissingOrBoundToDifferentClaim()
    {
        var bus = new EvidenceBus();
        Publish(bus, "obs-framework", "inspect", MissionSpecialistKind.StaticAnalysis,
            EvidenceObservationClass.Observed, EvidenceSensitivity.Public, "framework", "ASP.NET", "ev-framework");
        var projector = new TrustedKnowledgeBlueprintProjector(bus);

        Assert.Throws<InvalidDataException>(() => projector.Project(new MissionKnowledgeItem(
            "framework", EvidenceFusionState.Convergent,
            Knowledge(KnowledgeTrustState.Trusted, ["obs-missing"]))));
        Assert.Throws<InvalidDataException>(() => projector.Project(new MissionKnowledgeItem(
            "runtime", EvidenceFusionState.Convergent,
            Knowledge(KnowledgeTrustState.Trusted, ["obs-framework"]))));
    }

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
}
