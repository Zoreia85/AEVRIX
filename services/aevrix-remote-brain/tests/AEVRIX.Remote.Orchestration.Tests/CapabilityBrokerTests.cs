using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class CapabilityBrokerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 18, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Rank_PrefersHigherQualityReliableProvider()
    {
        var broker = new CapabilityBroker();
        broker.Register(Provider("vision-a", quality: 0.95, reliability: 0.99, latency: 900));
        broker.Register(Provider("vision-b", quality: 0.82, reliability: 0.94, latency: 250));

        var ranked = broker.Rank("vision-analysis", Now, TimeSpan.FromMinutes(5));

        Assert.AreEqual(2, ranked.Count);
        Assert.AreEqual("vision-a", ranked[0].ProviderId);
        Assert.IsTrue(ranked[0].Score > ranked[1].Score);
    }

    [TestMethod]
    public void ConsecutiveFailures_DegradeThenRemovePrimaryFromSelection()
    {
        var broker = new CapabilityBroker();
        broker.Register(Provider("primary", quality: 0.98, reliability: 0.99, latency: 200));
        broker.Register(Provider("backup", quality: 0.80, reliability: 0.96, latency: 300));

        Assert.AreEqual("primary", broker.SelectBest("vision-analysis", Now, TimeSpan.FromMinutes(5)).ProviderId);

        var first = broker.RecordOutcome("primary", false, 0, 500, Now.AddSeconds(1));
        Assert.AreEqual(CapabilityHealthState.Degraded, first.Health);

        broker.RecordOutcome("primary", false, 0, 500, Now.AddSeconds(2));
        var third = broker.RecordOutcome("primary", false, 0, 500, Now.AddSeconds(3));

        Assert.AreEqual(CapabilityHealthState.Unavailable, third.Health);
        Assert.AreEqual("backup", broker.SelectBest("vision-analysis", Now.AddSeconds(3), TimeSpan.FromMinutes(5)).ProviderId);
    }

    [TestMethod]
    public void SuccessfulOutcome_RecoversUnavailableProvider()
    {
        var broker = new CapabilityBroker();
        broker.Register(Provider("provider-a", quality: 0.90, reliability: 0.90, latency: 300));

        broker.RecordOutcome("provider-a", false, 0, 300, Now.AddSeconds(1));
        broker.RecordOutcome("provider-a", false, 0, 300, Now.AddSeconds(2));
        broker.RecordOutcome("provider-a", false, 0, 300, Now.AddSeconds(3));
        Assert.AreEqual(CapabilityHealthState.Unavailable, broker.Get("provider-a").Health);

        var recovered = broker.RecordOutcome("provider-a", true, 0.92, 250, Now.AddSeconds(4));

        Assert.AreEqual(CapabilityHealthState.Healthy, recovered.Health);
        Assert.AreEqual(0, recovered.ConsecutiveFailures);
        Assert.AreEqual("provider-a", broker.SelectBest("vision-analysis", Now.AddSeconds(4), TimeSpan.FromMinutes(5)).ProviderId);
    }

    [TestMethod]
    public void QuarantinedProvider_RemainsExcludedEvenAfterSuccessfulOutcome()
    {
        var broker = new CapabilityBroker();
        broker.Register(Provider("provider-a", quality: 0.99, reliability: 0.99, latency: 100));
        broker.Register(Provider("provider-b", quality: 0.70, reliability: 0.90, latency: 400));
        broker.SetQuarantined("provider-a", true);

        var outcome = broker.RecordOutcome("provider-a", true, 1.0, 80, Now.AddSeconds(1));

        Assert.AreEqual(CapabilityHealthState.Quarantined, outcome.Health);
        Assert.AreEqual("provider-b", broker.SelectBest("vision-analysis", Now.AddSeconds(1), TimeSpan.FromMinutes(5)).ProviderId);
    }

    [TestMethod]
    public void Rank_FailsClosedForStaleUnapprovedOrDisabledProviders()
    {
        var broker = new CapabilityBroker();
        broker.Register(Provider("stale", lastObservedAt: Now.AddHours(-2)));
        broker.Register(Provider("unapproved", approval: CapabilityApprovalState.Unreviewed));
        broker.Register(Provider("disabled", enabled: false));

        var ranked = broker.Rank("vision-analysis", Now, TimeSpan.FromMinutes(5));

        Assert.AreEqual(0, ranked.Count);
        Assert.Throws<InvalidOperationException>(() =>
            broker.SelectBest("vision-analysis", Now, TimeSpan.FromMinutes(5)));
    }

    [TestMethod]
    public void RecordOutcome_RejectsOutOfOrderTelemetry()
    {
        var broker = new CapabilityBroker();
        broker.Register(Provider("provider-a"));

        Assert.Throws<InvalidOperationException>(() =>
            broker.RecordOutcome("provider-a", true, 0.9, 200, Now.AddSeconds(-1)));
    }

    private static CapabilityProviderSnapshot Provider(
        string providerId,
        double quality = 0.90,
        double reliability = 0.95,
        double latency = 300,
        DateTimeOffset? lastObservedAt = null,
        CapabilityApprovalState approval = CapabilityApprovalState.Approved,
        bool enabled = true) =>
        new(
            ProviderId: providerId,
            Capability: "vision-analysis",
            Approval: approval,
            Health: CapabilityHealthState.Healthy,
            Enabled: enabled,
            QualityScore: quality,
            ReliabilityScore: reliability,
            P95LatencyMilliseconds: latency,
            ConsecutiveFailures: 0,
            LastObservedAt: lastObservedAt ?? Now);
}
