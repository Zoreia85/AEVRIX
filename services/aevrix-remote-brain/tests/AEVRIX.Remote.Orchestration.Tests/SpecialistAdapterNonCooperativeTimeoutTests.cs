using System.Diagnostics;
using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class SpecialistAdapterNonCooperativeTimeoutTests
{
    [TestMethod]
    public async Task NonCooperativeAdapter_CannotHoldSpecialistSlotPastAttemptBudget()
    {
        var now = new DateTimeOffset(2026, 8, 15, 3, 10, 0, TimeSpan.Zero);
        var broker = new CapabilityBroker();
        broker.Register(Snapshot("ignores-cancel", .99, now));
        broker.Register(Snapshot("backup", .90, now));
        var observer = new Observer();
        var specialist = new AdaptiveMissionSpecialist(
            MissionSpecialistKind.StaticAnalysis,
            "specialist-static-analysis",
            broker,
            [new IgnoringCancellationAdapter(), new BackupAdapter()],
            timeProvider: new FixedTime(now),
            executionPolicy: new SpecialistAdapterExecutionPolicy(TimeSpan.FromMilliseconds(25)),
            observer: observer);
        var stopwatch = Stopwatch.StartNew();

        var result = await specialist.ExecuteAsync(Context());

        stopwatch.Stop();
        Assert.AreEqual(.90, result.Confidence, .0001);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200));
        Assert.AreEqual(SpecialistAdapterAttemptOutcome.TimedOut, observer.Items[0].Outcome);
        Assert.AreEqual(1, broker.Get("ignores-cancel").ConsecutiveFailures);
    }

    private static CapabilityProviderSnapshot Snapshot(
        string id,
        double quality,
        DateTimeOffset now) =>
        new(
            id,
            "specialist-static-analysis",
            CapabilityApprovalState.Approved,
            CapabilityHealthState.Healthy,
            true,
            quality,
            .99,
            10,
            0,
            now);

    private static SpecialistExecutionContext Context() => new(
        "mission-noncoop",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "target-001",
        new MissionTaskSpec(
            "static-task",
            MissionSpecialistKind.StaticAnalysis,
            "Analyze authorized evidence under a hard wait budget.",
            ["ev-001"],
            []),
        new Dictionary<string, SpecialistTaskResult>());

    private sealed class IgnoringCancellationAdapter : IMissionSpecialistProviderAdapter
    {
        public string ProviderId => "ignores-cancel";
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;

        public async Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(500, CancellationToken.None);
            return new SpecialistExecutionOutput("Late output.", .99, context.Task.EvidenceIds, ["artifact-late"]);
        }
    }

    private sealed class BackupAdapter : IMissionSpecialistProviderAdapter
    {
        public string ProviderId => "backup";
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SpecialistExecutionOutput("Backup output.", .90, context.Task.EvidenceIds, ["artifact-backup"]));
    }

    private sealed class Observer : ISpecialistAdapterAttemptObserver
    {
        public List<SpecialistAdapterAttemptTelemetry> Items { get; } = [];

        public ValueTask ObserveAsync(
            SpecialistAdapterAttemptTelemetry telemetry,
            CancellationToken cancellationToken = default)
        {
            Items.Add(telemetry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
