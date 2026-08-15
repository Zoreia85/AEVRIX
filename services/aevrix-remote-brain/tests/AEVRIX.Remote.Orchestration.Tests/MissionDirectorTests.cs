using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class MissionDirectorTests
{
    [TestMethod]
    public async Task ExecuteAsync_RespectsDependenciesAndProducesOrderedResults()
    {
        var log = new List<string>();
        var director = new MissionDirector([
            new StubSpecialist(MissionSpecialistKind.StaticAnalysis, log),
            new StubSpecialist(MissionSpecialistKind.Reconstruction, log)
        ], new FixedTimeProvider());

        var plan = Plan([
            Task("inspect", MissionSpecialistKind.StaticAnalysis, evidenceIds: ["ev-001"]),
            Task("reconstruct", MissionSpecialistKind.Reconstruction, dependsOn: ["inspect"], evidenceIds: ["ev-001"])
        ]);

        var result = await director.ExecuteAsync(plan);

        Assert.IsTrue(result.RequiredTasksSucceeded);
        CollectionAssert.AreEqual(new[] { "inspect", "reconstruct" }, result.TaskResults.Select(item => item.TaskId).ToArray());
        CollectionAssert.AreEqual(new[] { "start:inspect", "end:inspect", "start:reconstruct", "end:reconstruct" }, log.ToArray());
    }

    [TestMethod]
    public async Task ExecuteAsync_BlocksDependentTaskWhenRequiredDependencyFails()
    {
        var log = new List<string>();
        var director = new MissionDirector([
            new StubSpecialist(MissionSpecialistKind.DynamicAnalysis, log, failTaskId: "probe"),
            new StubSpecialist(MissionSpecialistKind.Documentation, log)
        ], new FixedTimeProvider());

        var result = await director.ExecuteAsync(Plan([
            Task("probe", MissionSpecialistKind.DynamicAnalysis),
            Task("report", MissionSpecialistKind.Documentation, dependsOn: ["probe"])
        ]));

        Assert.IsFalse(result.RequiredTasksSucceeded);
        Assert.AreEqual(MissionTaskState.Failed, result.TaskResults[0].State);
        Assert.AreEqual(MissionTaskState.Blocked, result.TaskResults[1].State);
        Assert.AreEqual("DependencyFailure", result.TaskResults[1].ErrorType);
        Assert.IsFalse(log.Contains("start:report"));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsEvidenceEscalationOutsideTaskBoundary()
    {
        var director = new MissionDirector([
            new StubSpecialist(MissionSpecialistKind.NetworkBehavior, [], extraEvidenceId: "ev-forged")
        ], new FixedTimeProvider());

        var result = await director.ExecuteAsync(Plan([
            Task("network", MissionSpecialistKind.NetworkBehavior, evidenceIds: ["ev-001"])
        ]));

        Assert.IsFalse(result.RequiredTasksSucceeded);
        Assert.AreEqual(MissionTaskState.Failed, result.TaskResults.Single().State);
        Assert.AreEqual(nameof(InvalidDataException), result.TaskResults.Single().ErrorType);
    }

    [TestMethod]
    public async Task ExecuteAsync_FailsClosedWhenRequiredSpecialistIsMissing()
    {
        var director = new MissionDirector([], new FixedTimeProvider());
        var plan = Plan([Task("vision", MissionSpecialistKind.VisionOcr)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => director.ExecuteAsync(plan));
    }

    [TestMethod]
    public void Validate_RejectsDependencyCycles()
    {
        var plan = Plan([
            Task("task-a", MissionSpecialistKind.StaticAnalysis, dependsOn: ["task-b"]),
            Task("task-b", MissionSpecialistKind.DynamicAnalysis, dependsOn: ["task-a"])
        ]);

        Assert.Throws<ArgumentException>(plan.Validate);
    }

    [TestMethod]
    public async Task ExecuteAsync_EnforcesMaximumConcurrency()
    {
        var tracker = new ConcurrencyTracker();
        var director = new MissionDirector([
            new StubSpecialist(MissionSpecialistKind.StaticAnalysis, [], tracker: tracker),
            new StubSpecialist(MissionSpecialistKind.DynamicAnalysis, [], tracker: tracker),
            new StubSpecialist(MissionSpecialistKind.StructuralAnalysis, [], tracker: tracker)
        ], new FixedTimeProvider());

        var plan = Plan([
            Task("static", MissionSpecialistKind.StaticAnalysis),
            Task("dynamic", MissionSpecialistKind.DynamicAnalysis),
            Task("structure", MissionSpecialistKind.StructuralAnalysis)
        ], maximumConcurrency: 2);

        var result = await director.ExecuteAsync(plan);

        Assert.IsTrue(result.RequiredTasksSucceeded);
        Assert.IsTrue(tracker.MaximumObserved <= 2);
        Assert.AreEqual(2, tracker.MaximumObserved);
    }

    private static MissionPlan Plan(IReadOnlyList<MissionTaskSpec> tasks, int maximumConcurrency = 4) =>
        new(
            MissionId: "mission-001",
            ProjectId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TargetId: "target-001",
            Tasks: tasks,
            MaximumConcurrency: maximumConcurrency);

    private static MissionTaskSpec Task(
        string id,
        MissionSpecialistKind specialist,
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? evidenceIds = null) =>
        new(
            TaskId: id,
            Specialist: specialist,
            Objective: $"Execute {id} analysis.",
            EvidenceIds: evidenceIds ?? ["ev-001"],
            DependsOn: dependsOn ?? []);

    private sealed class StubSpecialist : IMissionSpecialist
    {
        private readonly List<string> _log;
        private readonly string? _failTaskId;
        private readonly string? _extraEvidenceId;
        private readonly ConcurrencyTracker? _tracker;

        public StubSpecialist(
            MissionSpecialistKind kind,
            List<string> log,
            string? failTaskId = null,
            string? extraEvidenceId = null,
            ConcurrencyTracker? tracker = null)
        {
            Kind = kind;
            _log = log;
            _failTaskId = failTaskId;
            _extraEvidenceId = extraEvidenceId;
            _tracker = tracker;
        }

        public MissionSpecialistKind Kind { get; }

        public async Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            _log.Add($"start:{context.Task.TaskId}");
            _tracker?.Enter();
            try
            {
                await System.Threading.Tasks.Task.Delay(30, cancellationToken);
                if (string.Equals(context.Task.TaskId, _failTaskId, StringComparison.Ordinal))
                {
                    throw new IOException("simulated specialist failure");
                }

                var evidence = context.Task.EvidenceIds.ToList();
                if (_extraEvidenceId is not null)
                {
                    evidence.Add(_extraEvidenceId);
                }

                return new SpecialistExecutionOutput(
                    Summary: $"Completed {context.Task.TaskId}.",
                    Confidence: 0.93,
                    EvidenceIds: evidence,
                    ArtifactIds: [$"artifact-{context.Task.TaskId}"]);
            }
            finally
            {
                _tracker?.Exit();
                _log.Add($"end:{context.Task.TaskId}");
            }
        }
    }

    private sealed class ConcurrencyTracker
    {
        private int _current;
        private int _maximum;

        public int MaximumObserved => Volatile.Read(ref _maximum);

        public void Enter()
        {
            var current = Interlocked.Increment(ref _current);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximum);
                if (current <= maximum || Interlocked.CompareExchange(ref _maximum, current, maximum) == maximum)
                {
                    return;
                }
            }
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 14, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
