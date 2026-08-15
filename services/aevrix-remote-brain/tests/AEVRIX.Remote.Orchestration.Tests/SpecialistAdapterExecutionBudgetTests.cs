using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class SpecialistAdapterExecutionBudgetTests
{
    [TestMethod]
    public async Task Timeout_DegradesPrimaryAndFailsOverToBackup()
    {
        var now = Now();
        var broker = Broker(now, ("slow", .99, .99, 10), ("backup", .90, .95, 20));
        var observer = new Observer();
        var specialist = Create(
            broker,
            now,
            [new DelayedAdapter("slow", TimeSpan.FromMilliseconds(120)), new ImmediateAdapter("backup", .91)],
            observer,
            new SpecialistAdapterExecutionPolicy(TimeSpan.FromMilliseconds(25)));

        var result = await specialist.ExecuteAsync(Context());

        Assert.AreEqual(.91, result.Confidence, .0001);
        Assert.AreEqual(1, broker.Get("slow").ConsecutiveFailures);
        Assert.AreEqual(CapabilityHealthState.Degraded, broker.Get("slow").Health);
        Assert.AreEqual(SpecialistAdapterAttemptOutcome.TimedOut, observer.Items[0].Outcome);
        Assert.AreEqual(SpecialistAdapterAttemptOutcome.Succeeded, observer.Items[1].Outcome);
    }

    [TestMethod]
    public async Task TimeoutWithoutFailover_StopsBeforeBackup()
    {
        var now = Now();
        var broker = Broker(now, ("slow", .99, .99, 10), ("backup", .90, .95, 20));
        var backup = new ImmediateAdapter("backup", .91);
        var specialist = Create(
            broker,
            now,
            [new DelayedAdapter("slow", TimeSpan.FromMilliseconds(120)), backup],
            new Observer(),
            new SpecialistAdapterExecutionPolicy(TimeSpan.FromMilliseconds(25), FailoverOnTimeout: false));

        await Assert.ThrowsAsync<TimeoutException>(() => specialist.ExecuteAsync(Context()));

        Assert.AreEqual(0, backup.Calls);
        Assert.AreEqual(1, broker.Get("slow").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task CallerCancellation_RemainsAuthoritativeAndDoesNotDamageHealth()
    {
        var now = Now();
        var broker = Broker(now, ("slow", .99, .99, 10));
        var observer = new Observer();
        var specialist = Create(
            broker,
            now,
            [new DelayedAdapter("slow", TimeSpan.FromSeconds(2))],
            observer,
            new SpecialistAdapterExecutionPolicy(TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => specialist.ExecuteAsync(Context(), cancellation.Token));

        Assert.AreEqual(0, broker.Get("slow").ConsecutiveFailures);
        Assert.AreEqual(0, observer.Items.Count);
    }

    [TestMethod]
    public async Task TelemetrySinkFailure_CannotChangeSuccessfulMissionResult()
    {
        var now = Now();
        var broker = Broker(now, ("primary", .99, .99, 10));
        var specialist = Create(
            broker,
            now,
            [new ImmediateAdapter("primary", .94)],
            new ThrowingObserver(),
            new SpecialistAdapterExecutionPolicy(TimeSpan.FromSeconds(1)));

        var result = await specialist.ExecuteAsync(Context());

        Assert.AreEqual(.94, result.Confidence, .0001);
        Assert.AreEqual(0, broker.Get("primary").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task EvidenceBoundaryRejection_IsClassifiedAndCanFailOver()
    {
        var now = Now();
        var broker = Broker(now, ("forged", .99, .99, 10), ("backup", .90, .95, 20));
        var observer = new Observer();
        var specialist = Create(
            broker,
            now,
            [new ForgingAdapter("forged"), new ImmediateAdapter("backup", .90)],
            observer,
            new SpecialistAdapterExecutionPolicy(TimeSpan.FromSeconds(1)));

        var result = await specialist.ExecuteAsync(Context());

        CollectionAssert.AreEqual(new[] { "ev-001" }, result.EvidenceIds.ToArray());
        Assert.AreEqual(SpecialistAdapterAttemptOutcome.EvidenceBoundaryRejected, observer.Items[0].Outcome);
        Assert.AreEqual(1, broker.Get("forged").ConsecutiveFailures);
    }

    [TestMethod]
    public void Policy_RejectsUnboundedOrTrivialTimeouts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SpecialistAdapterExecutionPolicy(TimeSpan.FromMilliseconds(1)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SpecialistAdapterExecutionPolicy(TimeSpan.FromHours(1)).Validate());
    }

    private static AdaptiveMissionSpecialist Create(
        CapabilityBroker broker,
        DateTimeOffset now,
        IEnumerable<IMissionSpecialistProviderAdapter> providers,
        ISpecialistAdapterAttemptObserver observer,
        SpecialistAdapterExecutionPolicy policy) =>
        new(
            MissionSpecialistKind.StaticAnalysis,
            "specialist-static-analysis",
            broker,
            providers,
            timeProvider: new FixedTime(now),
            executionPolicy: policy,
            observer: observer);

    private static CapabilityBroker Broker(
        DateTimeOffset now,
        params (string Id, double Quality, double Reliability, double Latency)[] items)
    {
        var broker = new CapabilityBroker();
        foreach (var item in items)
        {
            broker.Register(new CapabilityProviderSnapshot(
                item.Id,
                "specialist-static-analysis",
                CapabilityApprovalState.Approved,
                CapabilityHealthState.Healthy,
                true,
                item.Quality,
                item.Reliability,
                item.Latency,
                0,
                now));
        }

        return broker;
    }

    private static SpecialistExecutionContext Context() => new(
        "mission-budget",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "target-001",
        new MissionTaskSpec(
            "static-task",
            MissionSpecialistKind.StaticAnalysis,
            "Analyze authorized evidence under an operational budget.",
            ["ev-001"],
            []),
        new Dictionary<string, SpecialistTaskResult>());

    private static DateTimeOffset Now() =>
        new(2026, 8, 15, 3, 0, 0, TimeSpan.Zero);

    private sealed class ImmediateAdapter(string id, double confidence)
        : IMissionSpecialistProviderAdapter
    {
        public string ProviderId => id;
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;
        public int Calls { get; private set; }

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new SpecialistExecutionOutput(
                $"Adapter {id} completed.", confidence, context.Task.EvidenceIds, [$"artifact-{id}"]));
        }
    }

    private sealed class DelayedAdapter(string id, TimeSpan delay)
        : IMissionSpecialistProviderAdapter
    {
        public string ProviderId => id;
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;

        public async Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return new SpecialistExecutionOutput(
                $"Adapter {id} completed.", .90, context.Task.EvidenceIds, [$"artifact-{id}"]);
        }
    }

    private sealed class ForgingAdapter(string id)
        : IMissionSpecialistProviderAdapter
    {
        public string ProviderId => id;
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SpecialistExecutionOutput(
                "Forged evidence output.", .99, ["ev-001", "ev-forged"], [$"artifact-{id}"]));
    }

    private sealed class Observer : ISpecialistAdapterAttemptObserver
    {
        public List<SpecialistAdapterAttemptTelemetry> Items { get; } = [];

        public ValueTask ObserveAsync(
            SpecialistAdapterAttemptTelemetry telemetry,
            CancellationToken cancellationToken = default)
        {
            Items.Add(telemetry.Validate());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : ISpecialistAdapterAttemptObserver
    {
        public ValueTask ObserveAsync(
            SpecialistAdapterAttemptTelemetry telemetry,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("telemetry sink unavailable"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
