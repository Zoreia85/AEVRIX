using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AdaptiveModelCouncilProviderTests
{
    [TestMethod]
    public async Task AnalyzeAsync_UsesHighestRankedApprovedProvider()
    {
        var now = new DateTimeOffset(2026, 8, 14, 19, 0, 0, TimeSpan.Zero);
        var broker = new CapabilityBroker();
        broker.Register(Snapshot("primary", quality: 0.95, reliability: 0.98, latency: 80, now));
        broker.Register(Snapshot("backup", quality: 0.80, reliability: 0.90, latency: 120, now));

        var primary = new StubProvider("primary", confidence: 0.94);
        var backup = new StubProvider("backup", confidence: 0.88);
        var council = new AdaptiveModelCouncilProvider(
            broker,
            [primary, backup],
            timeProvider: new FixedTimeProvider(now));

        var result = await council.AnalyzeAsync(Task());

        Assert.AreEqual("primary", result.ProviderId);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(0, backup.CallCount);
        Assert.AreEqual(0, broker.Get("primary").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task AnalyzeAsync_FailsOverWhenTopProviderThrows()
    {
        var now = new DateTimeOffset(2026, 8, 14, 19, 5, 0, TimeSpan.Zero);
        var broker = new CapabilityBroker();
        broker.Register(Snapshot("primary", quality: 0.95, reliability: 0.98, latency: 80, now));
        broker.Register(Snapshot("backup", quality: 0.80, reliability: 0.90, latency: 120, now));

        var primary = new StubProvider("primary", exception: new IOException("provider offline"));
        var backup = new StubProvider("backup", confidence: 0.91);
        var council = new AdaptiveModelCouncilProvider(
            broker,
            [primary, backup],
            timeProvider: new FixedTimeProvider(now));

        var result = await council.AnalyzeAsync(Task());

        Assert.AreEqual("backup", result.ProviderId);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(1, backup.CallCount);
        Assert.AreEqual(1, broker.Get("primary").ConsecutiveFailures);
        Assert.AreEqual(CapabilityHealthState.Degraded, broker.Get("primary").Health);
    }

    [TestMethod]
    public async Task AnalyzeAsync_RejectsProviderIdentitySpoofAndFailsOver()
    {
        var now = new DateTimeOffset(2026, 8, 14, 19, 10, 0, TimeSpan.Zero);
        var broker = new CapabilityBroker();
        broker.Register(Snapshot("primary", quality: 0.95, reliability: 0.98, latency: 80, now));
        broker.Register(Snapshot("backup", quality: 0.80, reliability: 0.90, latency: 120, now));

        var primary = new StubProvider("primary", confidence: 0.95, candidateProviderId: "forged-provider");
        var backup = new StubProvider("backup", confidence: 0.90);
        var council = new AdaptiveModelCouncilProvider(
            broker,
            [primary, backup],
            timeProvider: new FixedTimeProvider(now));

        var result = await council.AnalyzeAsync(Task());

        Assert.AreEqual("backup", result.ProviderId);
        Assert.AreEqual(1, broker.Get("primary").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task AnalyzeAsync_FailsClosedWhenNoApprovedHealthyProviderExists()
    {
        var now = new DateTimeOffset(2026, 8, 14, 19, 15, 0, TimeSpan.Zero);
        var broker = new CapabilityBroker();
        broker.Register(Snapshot(
            "primary",
            quality: 0.95,
            reliability: 0.98,
            latency: 80,
            now,
            approval: CapabilityApprovalState.Pending));

        var council = new AdaptiveModelCouncilProvider(
            broker,
            [new StubProvider("primary", confidence: 0.95)],
            timeProvider: new FixedTimeProvider(now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => council.AnalyzeAsync(Task()));
    }

    [TestMethod]
    public async Task AnalyzeAsync_RespectsMaximumAttemptBudget()
    {
        var now = new DateTimeOffset(2026, 8, 14, 19, 20, 0, TimeSpan.Zero);
        var broker = new CapabilityBroker();
        broker.Register(Snapshot("one", 0.95, 0.99, 50, now));
        broker.Register(Snapshot("two", 0.90, 0.95, 70, now));
        broker.Register(Snapshot("three", 0.85, 0.90, 90, now));

        var one = new StubProvider("one", exception: new IOException("one"));
        var two = new StubProvider("two", exception: new IOException("two"));
        var three = new StubProvider("three", confidence: 0.92);
        var council = new AdaptiveModelCouncilProvider(
            broker,
            [one, two, three],
            new AdaptiveModelCouncilPolicy(MaximumAttempts: 2),
            new FixedTimeProvider(now));

        await Assert.ThrowsAsync<AggregateException>(() => council.AnalyzeAsync(Task()));
        Assert.AreEqual(1, one.CallCount);
        Assert.AreEqual(1, two.CallCount);
        Assert.AreEqual(0, three.CallCount);
    }

    [TestMethod]
    public async Task AnalyzeAsync_DoesNotSwallowCallerCancellation()
    {
        var now = new DateTimeOffset(2026, 8, 14, 19, 25, 0, TimeSpan.Zero);
        var broker = new CapabilityBroker();
        broker.Register(Snapshot("primary", 0.95, 0.99, 50, now));
        broker.Register(Snapshot("backup", 0.90, 0.95, 70, now));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var primary = new StubProvider("primary", confidence: 0.95);
        var backup = new StubProvider("backup", confidence: 0.90);
        var council = new AdaptiveModelCouncilProvider(
            broker,
            [primary, backup],
            timeProvider: new FixedTimeProvider(now));

        await Assert.ThrowsAsync<OperationCanceledException>(() => council.AnalyzeAsync(Task(), cts.Token));
        Assert.AreEqual(0, primary.CallCount);
        Assert.AreEqual(0, backup.CallCount);
    }

    private static CapabilityProviderSnapshot Snapshot(
        string providerId,
        double quality,
        double reliability,
        double latency,
        DateTimeOffset now,
        CapabilityApprovalState approval = CapabilityApprovalState.Approved) =>
        new(
            ProviderId: providerId,
            Capability: "model-analysis",
            Approval: approval,
            Health: CapabilityHealthState.Healthy,
            Enabled: true,
            QualityScore: quality,
            ReliabilityScore: reliability,
            P95LatencyMilliseconds: latency,
            ConsecutiveFailures: 0,
            LastObservedAt: now);

    private static AnalysisTask Task() =>
        new(
            TaskId: "task-adaptive-council-001",
            ProjectId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TargetId: "target-001",
            Objective: "Analyze the evidence and produce a bounded candidate.",
            EvidenceIds: ["evidence-001"],
            Context: new Dictionary<string, string>());

    private sealed class StubProvider : IAevrixModelProvider
    {
        private readonly double _confidence;
        private readonly Exception? _exception;
        private readonly string? _candidateProviderId;

        public StubProvider(
            string providerId,
            double confidence = 0.90,
            Exception? exception = null,
            string? candidateProviderId = null)
        {
            ProviderId = providerId;
            _confidence = confidence;
            _exception = exception;
            _candidateProviderId = candidateProviderId;
        }

        public string ProviderId { get; }
        public int CallCount { get; private set; }

        public Task<ModelAnalysisCandidate> AnalyzeAsync(AnalysisTask task, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            if (_exception is not null)
            {
                throw _exception;
            }

            return System.Threading.Tasks.Task.FromResult(new ModelAnalysisCandidate(
                ProviderId: _candidateProviderId ?? ProviderId,
                ProviderModelVersion: "test-model-1",
                Statement: "Candidate analysis grounded in supplied evidence.",
                Confidence: _confidence,
                Risk: ModelRiskLevel.Low,
                EvidenceIds: task.EvidenceIds,
                Assumptions: [],
                OpenQuestions: []));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
