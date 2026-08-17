using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AiProviderBudgetCurrencyIsolationTests
{
    [TestMethod]
    public void CurrencyMismatch_DoesNotReserveOrCommitProjectBudget()
    {
        var projectId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var manager = new AiProviderBudgetManager([
            new AiProjectBudgetPolicy(projectId, AiProviderBudgetProfile.Balanced, "USD", 1_000_000, 4, 10_000, 10_000)
        ]);

        var mismatchedEstimate = new AiProviderCallEstimate(
            projectId,
            "currency-mismatch",
            "remote-premium",
            AiProviderLocation.Remote,
            AiProviderBillingMode.ExternalMetered,
            "EUR",
            100_000,
            100,
            100,
            500);

        Assert.Throws<InvalidDataException>(() => manager.Reserve(mismatchedEstimate));

        var snapshot = manager.GetSnapshot(projectId);
        Assert.AreEqual(0L, snapshot.ReservedSpendMicros);
        Assert.AreEqual(0L, snapshot.CommittedSpendMicros);
        Assert.AreEqual(0, snapshot.ReservedMeteredCalls);
        Assert.AreEqual(0, snapshot.CommittedMeteredCalls);
    }
}
