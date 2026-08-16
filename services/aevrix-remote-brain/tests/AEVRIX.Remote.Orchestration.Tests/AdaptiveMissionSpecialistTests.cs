using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AdaptiveMissionSpecialistTests
{
    [TestMethod]
    public async Task RoutesToBestHealthyApprovedAdapter()
    {
        var now = Now();
        var broker = Broker(
            now,
            ("primary", .97, .99, 40),
            ("backup", .80, .90, 90));
        var primary = new Stub("primary", .96);
        var backup = new Stub("backup", .85);

        var result = await Specialist(
            broker,
            now,
            [primary, backup]).ExecuteAsync(Context());

        Assert.AreEqual(.96, result.Confidence, .0001);
        Assert.AreEqual(1, primary.Calls);
        Assert.AreEqual(0, backup.Calls);
    }

    [TestMethod]
    public async Task FailsOverAndFeedsFailureBackIntoBroker()
    {
        var now = Now();
        var broker = Broker(
            now,
            ("primary", .97, .99, 40),
            ("backup", .80, .90, 90));
        var primary = new Stub(
            "primary",
            exception: new IOException("offline"));
        var backup = new Stub("backup", .91);

        var result = await Specialist(
            broker,
            now,
            [primary, backup]).ExecuteAsync(Context());

        Assert.AreEqual(.91, result.Confidence, .0001);
        Assert.AreEqual(1, broker.Get("primary").ConsecutiveFailures);
        Assert.AreEqual(
            CapabilityHealthState.Degraded,
            broker.Get("primary").Health);
    }

    [TestMethod]
    public async Task RejectsEvidenceEscalationAndUsesBackup()
    {
        var now = Now();
        var broker = Broker(
            now,
            ("primary", .97, .99, 40),
            ("backup", .80, .90, 90));
        var primary = new Stub(
            "primary",
            .99,
            extraEvidence: "ev-forged");
        var backup = new Stub("backup", .88);

        var result = await Specialist(
            broker,
            now,
            [primary, backup]).ExecuteAsync(Context());

        CollectionAssert.AreEqual(
            new[] { "ev-001" },
            result.EvidenceIds.ToArray());
        Assert.AreEqual(1, broker.Get("primary").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task FailsClosedWhenNoEligibleAdapterExists()
    {
        var now = Now();
        var broker = new CapabilityBroker();
        broker.Register(Snapshot(
            "unreviewed",
            .99,
            .99,
            20,
            now,
            CapabilityApprovalState.Unreviewed));
        var specialist = Specialist(
            broker,
            now,
            [new Stub("unreviewed")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => specialist.ExecuteAsync(Context()));
    }

    [TestMethod]
    public async Task CallerCancellationDoesNotDamageProviderHealth()
    {
        var now = Now();
        var broker = Broker(
            now,
            ("primary", .99, .99, 10));
        var primary = new Stub("primary");
        var specialist = Specialist(broker, now, [primary]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => specialist.ExecuteAsync(
                Context(),
                cancellation.Token));

        Assert.AreEqual(0, primary.Calls);
        Assert.AreEqual(
            0,
            broker.Get("primary").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task RespectsBoundedFailoverBudget()
    {
        var now = Now();
        var broker = Broker(
            now,
            ("one", .99, .99, 10),
            ("two", .95, .98, 20),
            ("three", .90, .97, 30));
        var one = new Stub("one", exception: new IOException("one"));
        var two = new Stub("two", exception: new IOException("two"));
        var three = new Stub("three", .90);
        var specialist = new AdaptiveMissionSpecialist(
            MissionSpecialistKind.StaticAnalysis,
            "specialist-static-analysis",
            broker,
            [one, two, three],
            maximumAttempts: 2,
            timeProvider: new FixedTime(now));

        await Assert.ThrowsAsync<AggregateException>(
            () => specialist.ExecuteAsync(Context()));

        Assert.AreEqual(1, one.Calls);
        Assert.AreEqual(1, two.Calls);
        Assert.AreEqual(0, three.Calls);
    }

    private static AdaptiveMissionSpecialist Specialist(
        CapabilityBroker broker,
        DateTimeOffset now,
        IEnumerable<IMissionSpecialistProviderAdapter> adapters) =>
        new(
            MissionSpecialistKind.StaticAnalysis,
            "specialist-static-analysis",
            broker,
            adapters,
            timeProvider: new FixedTime(now));

    private static CapabilityBroker Broker(
        DateTimeOffset now,
        params (string Id, double Quality, double Reliability, double Latency)[] items)
    {
        var broker = new CapabilityBroker();
        foreach (var item in items)
        {
            broker.Register(Snapshot(
                item.Id,
                item.Quality,
                item.Reliability,
                item.Latency,
                now));
        }

        return broker;
    }

    private static CapabilityProviderSnapshot Snapshot(
        string id,
        double quality,
        double reliability,
        double latency,
        DateTimeOffset now,
        CapabilityApprovalState approval = CapabilityApprovalState.Approved) =>
        new(
            id,
            "specialist-static-analysis",
            approval,
            CapabilityHealthState.Healthy,
            true,
            quality,
            reliability,
            latency,
            0,
            now);

    private static SpecialistExecutionContext Context() =>
        new(
            "mission-adapter",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "target-001",
            new MissionTaskSpec(
                "static-task",
                MissionSpecialistKind.StaticAnalysis,
                "Analyze authorized evidence.",
                ["ev-001"],
                []),
            new Dictionary<string, SpecialistTaskResult>());

    private static DateTimeOffset Now() =>
        new(2026, 8, 15, 2, 0, 0, TimeSpan.Zero);

    private sealed class Stub(
        string id,
        double confidence = .90,
        Exception? exception = null,
        string? extraEvidence = null)
        : IMissionSpecialistProviderAdapter
    {
        public string ProviderId => id;
        public MissionSpecialistKind Kind =>
            MissionSpecialistKind.StaticAnalysis;
        public int Calls { get; private set; }

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;

            if (exception is not null)
            {
                throw exception;
            }

            var evidence = context.Task.EvidenceIds.ToList();
            if (extraEvidence is not null)
            {
                evidence.Add(extraEvidence);
            }

            return Task.FromResult(
                new SpecialistExecutionOutput(
                    $"Adapter {id} completed.",
                    confidence,
                    evidence,
                    [$"artifact-{id}"]));
        }
    }

    private sealed class FixedTime(DateTimeOffset now)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
