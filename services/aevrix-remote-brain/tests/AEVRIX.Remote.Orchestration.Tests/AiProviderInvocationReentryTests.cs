using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AiProviderInvocationReentryTests
{
    [TestMethod]
    public void StartedInvocation_CannotReserveSameRequestForSecondProviderCall()
    {
        var projectId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var policy = new AiProjectBudgetPolicy(
            projectId,
            AiProviderBudgetProfile.Balanced,
            "USD",
            1_000,
            1,
            1_000,
            1_000);
        var budgets = new AiProviderBudgetManager([policy]);
        var coordinator = new AiProviderInvocationCoordinator(budgets);
        var estimate = new AiProviderCallEstimate(
            projectId,
            "paid-reentry",
            "remote-premium",
            AiProviderLocation.Remote,
            AiProviderBillingMode.ExternalMetered,
            "USD",
            500,
            100,
            100,
            500);

        Assert.IsTrue(coordinator.Reserve(estimate).Allowed);
        coordinator.MarkInvocationStarted(projectId, estimate.RequestId);

        Assert.Throws<InvalidOperationException>(() => coordinator.Reserve(estimate));

        var snapshot = budgets.GetSnapshot(projectId);
        Assert.AreEqual(500L, snapshot.ReservedSpendMicros);
        Assert.AreEqual(1, snapshot.ReservedMeteredCalls);
    }
}
