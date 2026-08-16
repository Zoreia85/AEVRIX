using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class QirMissionDirectorSchedulingTests
{
    [TestMethod]
    public async Task ExecuteWithQirHintsAsync_ConsumesLimitedCapacityInPriorityOrder()
    {
        var starts = new List<string>();
        var director = TestProofBoundMissionDirector.Create([
            new OrderedSpecialist(MissionSpecialistKind.StaticAnalysis, starts),
            new OrderedSpecialist(MissionSpecialistKind.VisionOcr, starts),
            new OrderedSpecialist(MissionSpecialistKind.NetworkBehavior, starts)
        ]);
        var plan = Plan([
            Spec("static", MissionSpecialistKind.StaticAnalysis),
            Spec("vision", MissionSpecialistKind.VisionOcr),
            Spec("network", MissionSpecialistKind.NetworkBehavior)
        ], maximumConcurrency: 1);

        var result = await director.ExecuteWithQirHintsAsync(plan, [
            Hint(MissionSpecialistKind.NetworkBehavior, 0.95),
            Hint(MissionSpecialistKind.VisionOcr, 0.80)
        ]);

        CollectionAssert.AreEqual(new[] { "network", "vision", "static" }, starts.ToArray());
        CollectionAssert.AreEqual(
            new[] { "static", "vision", "network" },
            result.TaskResults.Select(item => item.TaskId).ToArray());
        Assert.IsTrue(result.RequiredTasksSucceeded);
    }

    [TestMethod]
    public async Task ExecuteWithQirHintsAsync_NeverLetsHintBypassDependency()
    {
        var starts = new List<string>();
        var director = TestProofBoundMissionDirector.Create([
            new OrderedSpecialist(MissionSpecialistKind.StaticAnalysis, starts),
            new OrderedSpecialist(MissionSpecialistKind.Reconstruction, starts)
        ]);
        var plan = Plan([
            Spec("inspect", MissionSpecialistKind.StaticAnalysis),
            Spec("reconstruct", MissionSpecialistKind.Reconstruction, ["inspect"])
        ], maximumConcurrency: 1);

        var result = await director.ExecuteWithQirHintsAsync(plan, [
            Hint(MissionSpecialistKind.Reconstruction, 1.0)
        ]);

        CollectionAssert.AreEqual(new[] { "inspect", "reconstruct" }, starts.ToArray());
        Assert.IsTrue(result.RequiredTasksSucceeded);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithoutHintsPreservesOriginalSchedulingOrder()
    {
        var starts = new List<string>();
        var director = TestProofBoundMissionDirector.Create([
            new OrderedSpecialist(MissionSpecialistKind.StaticAnalysis, starts),
            new OrderedSpecialist(MissionSpecialistKind.VisionOcr, starts)
        ]);
        var plan = Plan([
            Spec("static", MissionSpecialistKind.StaticAnalysis),
            Spec("vision", MissionSpecialistKind.VisionOcr)
        ], maximumConcurrency: 1);

        await director.ExecuteAsync(plan);

        CollectionAssert.AreEqual(new[] { "static", "vision" }, starts.ToArray());
    }

    [TestMethod]
    public async Task ExecuteWithQirHintsAsync_RejectsInvalidHintBeforeSpecialistExecution()
    {
        var starts = new List<string>();
        var director = TestProofBoundMissionDirector.Create([
            new OrderedSpecialist(MissionSpecialistKind.StaticAnalysis, starts)
        ]);
        var plan = Plan([Spec("static", MissionSpecialistKind.StaticAnalysis)], maximumConcurrency: 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => director.ExecuteWithQirHintsAsync(plan, [
            Hint(MissionSpecialistKind.StaticAnalysis, double.NaN)
        ]));

        Assert.AreEqual(0, starts.Count);
    }

    private static MissionPlan Plan(IReadOnlyList<MissionTaskSpec> tasks, int maximumConcurrency) => new(
        "mission-qir-scheduling",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "target-001",
        tasks,
        maximumConcurrency);

    private static MissionTaskSpec Spec(
        string id,
        MissionSpecialistKind kind,
        IReadOnlyList<string>? dependsOn = null) =>
        new(id, kind, $"Analyze {id}.", ["ev-001"], dependsOn ?? []);

    private static QirMissionHint Hint(MissionSpecialistKind kind, double score) =>
        new(kind, score, "qir-route", ["pattern-001"]);

    private sealed class OrderedSpecialist(
        MissionSpecialistKind kind,
        List<string> starts) : IMissionSpecialist
    {
        public MissionSpecialistKind Kind => kind;

        public async Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            starts.Add(context.Task.TaskId);
            await Task.Delay(5, cancellationToken);
            return new SpecialistExecutionOutput(
                $"Completed {context.Task.TaskId}.",
                0.95,
                context.Task.EvidenceIds,
                [$"artifact-{context.Task.TaskId}"]);
        }
    }
}
