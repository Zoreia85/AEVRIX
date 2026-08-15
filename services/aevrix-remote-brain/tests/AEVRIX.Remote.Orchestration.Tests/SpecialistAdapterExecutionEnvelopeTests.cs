using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class SpecialistAdapterExecutionEnvelopeTests
{
    [TestMethod]
    public async Task EnvelopeAwareAdapter_ReceivesGovernedEnvelope()
    {
        var now = Now();
        var broker = Broker(now, "sandboxed");
        var adapter = new GovernedAdapter(
            "sandboxed",
            new SpecialistAdapterExecutionProfile(
                AdapterNetworkScope.None,
                AdapterWorkspaceScope.ReadOnly,
                AgentIsolationLevel.Container));
        var envelope = Envelope();
        var specialist = Create(broker, now, [adapter], envelope);

        var result = await specialist.ExecuteAsync(Context());

        Assert.AreEqual(.94, result.Confidence, .0001);
        Assert.AreSame(envelope, adapter.LastEnvelope);
        Assert.AreEqual(0, broker.Get("sandboxed").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task LegacyAdapter_IsRejectedAndGovernedBackupCanRun()
    {
        var now = Now();
        var broker = Broker(now, "legacy", "backup");
        var observer = new Observer();
        var backup = new GovernedAdapter(
            "backup",
            new SpecialistAdapterExecutionProfile(
                AdapterNetworkScope.None,
                AdapterWorkspaceScope.ReadOnly,
                AgentIsolationLevel.VirtualMachine));
        var specialist = Create(
            broker,
            now,
            [new LegacyAdapter("legacy"), backup],
            Envelope(),
            observer);

        var result = await specialist.ExecuteAsync(Context());

        Assert.AreEqual(.94, result.Confidence, .0001);
        Assert.AreEqual(SpecialistAdapterAttemptOutcome.ExecutionEnvelopeRejected, observer.Items[0].Outcome);
        Assert.AreEqual(SpecialistAdapterAttemptOutcome.Succeeded, observer.Items[1].Outcome);
        Assert.AreEqual(1, broker.Get("legacy").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task AdapterWithBroaderNetworkScope_IsRejectedFailClosed()
    {
        var now = Now();
        var broker = Broker(now, "networked");
        var observer = new Observer();
        var specialist = Create(
            broker,
            now,
            [new GovernedAdapter(
                "networked",
                new SpecialistAdapterExecutionProfile(
                    AdapterNetworkScope.LoopbackOnly,
                    AdapterWorkspaceScope.ReadOnly,
                    AgentIsolationLevel.Container))],
            Envelope(network: AdapterNetworkScope.None),
            observer);

        await Assert.ThrowsAsync<AggregateException>(() => specialist.ExecuteAsync(Context()));

        Assert.AreEqual(SpecialistAdapterAttemptOutcome.ExecutionEnvelopeRejected, observer.Items.Single().Outcome);
        Assert.AreEqual(1, broker.Get("networked").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task AdapterWithWeakerIsolation_IsRejectedFailClosed()
    {
        var now = Now();
        var broker = Broker(now, "local");
        var specialist = Create(
            broker,
            now,
            [new GovernedAdapter(
                "local",
                new SpecialistAdapterExecutionProfile(
                    AdapterNetworkScope.None,
                    AdapterWorkspaceScope.ReadOnly,
                    AgentIsolationLevel.LocalProcess))],
            Envelope(isolation: AgentIsolationLevel.Container));

        await Assert.ThrowsAsync<AggregateException>(() => specialist.ExecuteAsync(Context()));
        Assert.AreEqual(1, broker.Get("local").ConsecutiveFailures);
    }

    [TestMethod]
    public async Task OutputByteBudget_IsEnforcedAndCanFailOver()
    {
        var now = Now();
        var broker = Broker(now, "oversized", "backup");
        var observer = new Observer();
        var profile = new SpecialistAdapterExecutionProfile(
            AdapterNetworkScope.None,
            AdapterWorkspaceScope.ReadOnly,
            AgentIsolationLevel.Container);
        var specialist = Create(
            broker,
            now,
            [
                new GovernedAdapter("oversized", profile, summary: new string('x', 2_000)),
                new GovernedAdapter("backup", profile)
            ],
            Envelope(maximumSummaryUtf8Bytes: 1_024),
            observer);

        var result = await specialist.ExecuteAsync(Context());

        Assert.AreEqual(.94, result.Confidence, .0001);
        Assert.AreEqual(SpecialistAdapterAttemptOutcome.OutputBudgetRejected, observer.Items[0].Outcome);
        Assert.AreEqual(SpecialistAdapterAttemptOutcome.Succeeded, observer.Items[1].Outcome);
        Assert.AreEqual(1, broker.Get("oversized").ConsecutiveFailures);
    }

    [TestMethod]
    public void Envelope_RejectsUnsafeBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Envelope(maximumSummaryUtf8Bytes: 100).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SpecialistAdapterExecutionEnvelope(
                AdapterNetworkScope.None,
                AdapterWorkspaceScope.ReadOnly,
                AgentIsolationLevel.Container,
                MaximumSummaryUtf8Bytes: 1_024,
                MaximumEvidenceIds: 2_001,
                MaximumArtifactIds: 1).Validate());
    }

    private static AdaptiveMissionSpecialist Create(
        CapabilityBroker broker,
        DateTimeOffset now,
        IEnumerable<IMissionSpecialistProviderAdapter> providers,
        SpecialistAdapterExecutionEnvelope envelope,
        ISpecialistAdapterAttemptObserver? observer = null) =>
        new(
            MissionSpecialistKind.StaticAnalysis,
            "specialist-static-analysis",
            broker,
            providers,
            timeProvider: new FixedTime(now),
            executionPolicy: new SpecialistAdapterExecutionPolicy(
                TimeSpan.FromSeconds(1),
                Envelope: envelope),
            observer: observer);

    private static SpecialistAdapterExecutionEnvelope Envelope(
        AdapterNetworkScope network = AdapterNetworkScope.None,
        AgentIsolationLevel isolation = AgentIsolationLevel.Container,
        int maximumSummaryUtf8Bytes = 8_000) =>
        new(
            network,
            AdapterWorkspaceScope.ReadOnly,
            isolation,
            maximumSummaryUtf8Bytes,
            MaximumEvidenceIds: 32,
            MaximumArtifactIds: 32);

    private static CapabilityBroker Broker(DateTimeOffset now, params string[] providers)
    {
        var broker = new CapabilityBroker();
        for (var index = 0; index < providers.Length; index++)
        {
            broker.Register(new CapabilityProviderSnapshot(
                providers[index],
                "specialist-static-analysis",
                CapabilityApprovalState.Approved,
                CapabilityHealthState.Healthy,
                true,
                .99 - (index * .05),
                .99 - (index * .03),
                10 + index,
                0,
                now));
        }

        return broker;
    }

    private static SpecialistExecutionContext Context() => new(
        "mission-envelope",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "target-001",
        new MissionTaskSpec(
            "static-task",
            MissionSpecialistKind.StaticAnalysis,
            "Analyze authorized evidence within a governed execution envelope.",
            ["ev-001"],
            []),
        new Dictionary<string, SpecialistTaskResult>());

    private static DateTimeOffset Now() =>
        new(2026, 8, 15, 3, 30, 0, TimeSpan.Zero);

    private sealed class GovernedAdapter(
        string id,
        SpecialistAdapterExecutionProfile profile,
        string? summary = null)
        : IExecutionEnvelopeAwareMissionSpecialistProviderAdapter
    {
        public string ProviderId => id;
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;
        public SpecialistAdapterExecutionProfile ExecutionProfile => profile;
        public SpecialistAdapterExecutionEnvelope? LastEnvelope { get; private set; }

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Governed adapters must be invoked with an execution envelope.");

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            SpecialistAdapterExecutionEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastEnvelope = envelope;
            return Task.FromResult(new SpecialistExecutionOutput(
                summary ?? $"Governed adapter {id} completed.",
                .94,
                context.Task.EvidenceIds,
                [$"artifact-{id}"]));
        }
    }

    private sealed class LegacyAdapter(string id) : IMissionSpecialistProviderAdapter
    {
        public string ProviderId => id;
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SpecialistExecutionOutput(
                "Legacy adapter should not execute under a governed envelope.",
                .99,
                context.Task.EvidenceIds,
                [$"artifact-{id}"]));
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

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
