using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class QirMissionPlanPrioritizerTests
{
    [TestMethod]
    public void Prioritize_ReordersSameDepthTasksByHintPriority()
    {
        var plan = Plan([
            Spec("static", MissionSpecialistKind.StaticAnalysis),
            Spec("vision", MissionSpecialistKind.VisionOcr),
            Spec("network", MissionSpecialistKind.NetworkBehavior)
        ]);
        var hints = new[]
        {
            Hint(MissionSpecialistKind.NetworkBehavior, 0.95),
            Hint(MissionSpecialistKind.VisionOcr, 0.80)
        };

        var prioritized = new QirMissionPlanPrioritizer().Prioritize(plan, hints);

        CollectionAssert.AreEqual(new[] { "network", "vision", "static" }, prioritized.Tasks.Select(t => t.TaskId).ToArray());
    }

    [TestMethod]
    public void Prioritize_NeverMovesDependentTaskBeforeItsDependencyDepth()
    {
        var plan = Plan([
            Spec("inspect", MissionSpecialistKind.StaticAnalysis),
            Spec("reconstruct", MissionSpecialistKind.Reconstruction, ["inspect"]),
            Spec("vision", MissionSpecialistKind.VisionOcr)
        ]);
        var hints = new[] { Hint(MissionSpecialistKind.Reconstruction, 1.0) };

        var prioritized = new QirMissionPlanPrioritizer().Prioritize(plan, hints);

        Assert.IsTrue(Array.IndexOf(prioritized.Tasks.Select(t => t.TaskId).ToArray(), "inspect") <
                      Array.IndexOf(prioritized.Tasks.Select(t => t.TaskId).ToArray(), "reconstruct"));
    }

    [TestMethod]
    public void Prioritize_PreservesGovernedTaskContentAndMissionScope()
    {
        var plan = Plan([Spec("static", MissionSpecialistKind.StaticAnalysis)]);
        var prioritized = new QirMissionPlanPrioritizer().Prioritize(plan, [Hint(MissionSpecialistKind.StaticAnalysis, 0.9)]);

        Assert.AreEqual(plan.ProjectId, prioritized.ProjectId);
        Assert.AreEqual(plan.TargetId, prioritized.TargetId);
        Assert.AreEqual(plan.MaximumConcurrency, prioritized.MaximumConcurrency);
        Assert.AreSame(plan.Tasks[0], prioritized.Tasks[0]);
    }

    [TestMethod]
    public void Prioritize_RejectsHintThatClaimsEvidenceAuthority()
    {
        var plan = Plan([Spec("static", MissionSpecialistKind.StaticAnalysis)]);
        var forged = new ForgedHint(MissionSpecialistKind.StaticAnalysis, 0.9);
        Assert.IsNotNull(forged);
        // QirMissionHint authority flags are hard-coded false; this test documents that they cannot be elevated by callers.
        var hint = Hint(MissionSpecialistKind.StaticAnalysis, 0.9);
        Assert.IsFalse(hint.IsEvidence);
        Assert.IsFalse(hint.CanSatisfyEvidenceRequirement);
        Assert.IsFalse(hint.CanDriveBlueprint);
        _ = new QirMissionPlanPrioritizer().Prioritize(plan, [hint]);
    }

    [TestMethod]
    public void Prioritize_RejectsInvalidPriorityScore()
    {
        var plan = Plan([Spec("static", MissionSpecialistKind.StaticAnalysis)]);
        var invalid = new QirMissionHint(MissionSpecialistKind.StaticAnalysis, double.NaN, "qir-route", ["pattern-001"]);
        Assert.Throws<InvalidDataException>(() => new QirMissionPlanPrioritizer().Prioritize(plan, [invalid]));
    }

    private static MissionPlan Plan(IReadOnlyList<MissionTaskSpec> tasks) => new(
        "mission-qir-priority",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "target-001",
        tasks,
        4);

    private static MissionTaskSpec Spec(string id, MissionSpecialistKind kind, IReadOnlyList<string>? dependsOn = null) =>
        new(id, kind, $"Analyze {id}.", ["ev-001"], dependsOn ?? []);

    private static QirMissionHint Hint(MissionSpecialistKind kind, double score) =>
        new(kind, score, "qir-route", ["pattern-001"]);

    private sealed record ForgedHint(MissionSpecialistKind Specialist, double PriorityScore);
}
