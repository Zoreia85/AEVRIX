using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class EvidenceFusionTests
{
    [TestMethod]
    public void PublishFromSpecialist_IsIdempotentButRejectsEvidenceIdMutation()
    {
        var bus = new EvidenceBus();
        var context = Context("task-static", MissionSpecialistKind.StaticAnalysis);
        var observation = Observation(
            "obs-001",
            context,
            claimValue: "ASP.NET Core",
            observationClass: EvidenceObservationClass.Observed);

        var first = bus.PublishFromSpecialist(context, observation);
        var second = bus.PublishFromSpecialist(context, observation);

        Assert.AreEqual(first, second);
        Assert.AreEqual(1, bus.Snapshot(context.ProjectId, context.TargetId).Count);

        var mutated = observation with { ClaimValue = "Node.js" };
        Assert.Throws<InvalidOperationException>(() => bus.PublishFromSpecialist(context, mutated));
    }

    [TestMethod]
    public void PublishFromSpecialist_RejectsCrossProjectAndEvidenceBoundaryEscalation()
    {
        var bus = new EvidenceBus();
        var context = Context("task-static", MissionSpecialistKind.StaticAnalysis);
        var wrongProject = Observation("obs-wrong-project", context) with
        {
            ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222")
        };

        Assert.Throws<InvalidDataException>(() => bus.PublishFromSpecialist(context, wrongProject));

        var escalated = Observation("obs-escalated", context) with
        {
            ParentEvidenceIds = ["input-999"]
        };
        Assert.Throws<InvalidDataException>(() => bus.PublishFromSpecialist(context, escalated));
    }

    [TestMethod]
    public void Validate_RejectsRawSecretsAndPublicPersonalData()
    {
        var context = Context("task-static", MissionSpecialistKind.StaticAnalysis);
        var rawSecret = Observation("obs-secret", context) with
        {
            ContainsRawSecretMaterial = true
        };
        Assert.Throws<InvalidDataException>(rawSecret.Validate);

        var publicPersonalData = Observation("obs-personal", context) with
        {
            ContainsPersonalData = true,
            Sensitivity = EvidenceSensitivity.Public
        };
        Assert.Throws<InvalidDataException>(publicPersonalData.Validate);
    }

    [TestMethod]
    public void GlobalLearningSnapshot_OnlyReturnsSanitizedPublicObservedEvidence()
    {
        var bus = new EvidenceBus();
        var publicContext = Context("task-static", MissionSpecialistKind.StaticAnalysis);
        var privateContext = Context("task-dynamic", MissionSpecialistKind.DynamicAnalysis);

        bus.PublishFromSpecialist(publicContext, Observation("obs-public", publicContext));
        bus.PublishFromSpecialist(privateContext, Observation("obs-private", privateContext) with
        {
            Sensitivity = EvidenceSensitivity.PersonalData,
            ContainsPersonalData = true
        });
        bus.PublishFromSpecialist(publicContext, Observation("obs-inferred", publicContext) with
        {
            ObservationClass = EvidenceObservationClass.Inferred
        });

        var eligible = bus.GlobalLearningEligibleSnapshot(publicContext.ProjectId, publicContext.TargetId);

        Assert.AreEqual(1, eligible.Count);
        Assert.AreEqual("obs-public", eligible.Single().EvidenceId);
    }

    [TestMethod]
    public void Fuse_ProducesConvergentCandidateFromIndependentSpecialists()
    {
        var staticContext = Context("task-static", MissionSpecialistKind.StaticAnalysis);
        var dynamicContext = Context("task-dynamic", MissionSpecialistKind.DynamicAnalysis);
        var observations = new[]
        {
            Observation("obs-static", staticContext, "ASP.NET Core", EvidenceObservationClass.Observed, 0.95),
            Observation("obs-dynamic", dynamicContext, "asp.net core", EvidenceObservationClass.ExperimentallyValidated, 0.92)
        };

        var candidate = new EvidenceFusionEngine().Fuse(
            staticContext.ProjectId,
            staticContext.TargetId,
            "runtime.framework",
            observations);

        Assert.AreEqual(EvidenceFusionState.Convergent, candidate.State);
        Assert.IsFalse(candidate.HasConflict);
        Assert.IsTrue(candidate.RequiresJudgeValidation);
        Assert.AreEqual("ASP.NET Core", candidate.PreferredValue);
        Assert.AreEqual(1, candidate.Alternatives.Count);
        Assert.AreEqual(2, candidate.Alternatives.Single().IndependentSourceCount);
        Assert.AreEqual(2, candidate.Alternatives.Single().Specialists.Count);
        Assert.IsTrue(candidate.EligibleForGlobalLearning);
        Assert.IsTrue(candidate.Confidence > 0.85);
    }

    [TestMethod]
    public void Fuse_PreservesContradictionInsteadOfChoosingAWinner()
    {
        var staticContext = Context("task-static", MissionSpecialistKind.StaticAnalysis);
        var dynamicContext = Context("task-dynamic", MissionSpecialistKind.DynamicAnalysis);
        var observations = new[]
        {
            Observation("obs-static", staticContext, "ASP.NET Core", EvidenceObservationClass.Observed, 0.97),
            Observation("obs-dynamic", dynamicContext, "Node.js", EvidenceObservationClass.ExperimentallyValidated, 0.96)
        };

        var candidate = new EvidenceFusionEngine().Fuse(
            staticContext.ProjectId,
            staticContext.TargetId,
            "runtime.framework",
            observations);

        Assert.AreEqual(EvidenceFusionState.Contested, candidate.State);
        Assert.IsTrue(candidate.HasConflict);
        Assert.IsNull(candidate.PreferredValue);
        Assert.AreEqual(2, candidate.Alternatives.Count);
        Assert.IsFalse(candidate.EligibleForGlobalLearning);
        Assert.IsTrue(candidate.RequiresJudgeValidation);
    }

    [TestMethod]
    public void Fuse_RejectsMixedProjectInputs()
    {
        var context = Context("task-static", MissionSpecialistKind.StaticAnalysis);
        var otherContext = Context(
            "task-dynamic",
            MissionSpecialistKind.DynamicAnalysis,
            Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var observations = new[]
        {
            Observation("obs-static", context),
            Observation("obs-other", otherContext)
        };

        Assert.Throws<InvalidDataException>(() => new EvidenceFusionEngine().Fuse(
            context.ProjectId,
            context.TargetId,
            "runtime.framework",
            observations));
    }

    private static SpecialistExecutionContext Context(
        string taskId,
        MissionSpecialistKind specialist,
        Guid? projectId = null)
    {
        var task = new MissionTaskSpec(
            TaskId: taskId,
            Specialist: specialist,
            Objective: "Produce a structured evidence observation.",
            EvidenceIds: ["input-001"],
            DependsOn: []);

        return new SpecialistExecutionContext(
            MissionId: "mission-001",
            ProjectId: projectId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TargetId: "target-001",
            Task: task,
            DependencyResults: new Dictionary<string, SpecialistTaskResult>());
    }

    private static EvidenceObservation Observation(
        string evidenceId,
        SpecialistExecutionContext context,
        string claimValue = "ASP.NET Core",
        EvidenceObservationClass observationClass = EvidenceObservationClass.Observed,
        double confidence = 0.93) =>
        new(
            EvidenceId: evidenceId,
            ProjectId: context.ProjectId,
            TargetId: context.TargetId,
            SourceTaskId: context.Task.TaskId,
            Specialist: context.Task.Specialist,
            ObservationClass: observationClass,
            Sensitivity: EvidenceSensitivity.Public,
            ClaimKey: "runtime.framework",
            ClaimValue: claimValue,
            Summary: "Structured observation produced by an authorized specialist.",
            Confidence: confidence,
            ContentSha256: new string('a', 64),
            ObservedAt: new DateTimeOffset(2026, 8, 14, 20, 40, 0, TimeSpan.Zero),
            SourceArtifactIds: ["artifact-001"],
            ParentEvidenceIds: ["input-001"]);
}
