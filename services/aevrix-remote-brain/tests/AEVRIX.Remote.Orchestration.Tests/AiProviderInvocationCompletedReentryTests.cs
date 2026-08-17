using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AiProviderInvocationCompletedReentryTests
{
    [TestMethod]
    public void CompletedMeteredRequest_CannotReserveAgain()
    {
        var projectId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var budgets = new AiProviderBudgetManager([
            new AiProjectBudgetPolicy(projectId, AiProviderBudgetProfile.Balanced, "USD", 1_000, 2, 1_000, 1_000)
        ]);
        var coordinator = new AiProviderInvocationCoordinator(budgets);
        var estimate = new AiProviderCallEstimate(
            projectId,
            "completed-request",
            "remote-premium",
            AiProviderLocation.Remote,
            AiProviderBillingMode.ExternalMetered,
            "USD",
            400,
            100,
            100,
            500);

        Assert.IsTrue(coordinator.Reserve(estimate).Allowed);
        coordinator.MarkInvocationStarted(projectId, estimate.RequestId);
        coordinator.Complete(new AiProviderUsageReceipt(
            projectId,
            estimate.RequestId,
            estimate.ProviderId,
            "USD",
            350,
            90,
            80));

        var reentry = coordinator.Reserve(estimate);

        Assert.IsFalse(reentry.Allowed);
        Assert.AreEqual(AiBudgetDenialReason.RequestAlreadyCompleted, reentry.DenialReason);
        var snapshot = budgets.GetSnapshot(projectId);
        Assert.AreEqual(350L, snapshot.CommittedSpendMicros);
        Assert.AreEqual(1, snapshot.CommittedMeteredCalls);
        Assert.AreEqual(0L, snapshot.ReservedSpendMicros);
    }
}
