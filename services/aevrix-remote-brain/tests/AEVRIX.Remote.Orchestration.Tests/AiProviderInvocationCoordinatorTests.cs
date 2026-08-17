using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AiProviderInvocationCoordinatorTests
{
    private static readonly Guid ProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [TestMethod]
    public void StartedMeteredInvocation_CannotReleaseReservationAndRiskDuplicateSpend()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 40, 0, TimeSpan.Zero));
        var budgets = new AiProviderBudgetManager([Policy()], time);
        var coordinator = new AiProviderInvocationCoordinator(budgets, time);

        var decision = coordinator.Reserve(Estimate("paid-1", 900));
        Assert.IsTrue(decision.Allowed);

        var started = coordinator.MarkInvocationStarted(ProjectId, "paid-1");
        Assert.AreEqual(AiProviderInvocationPhase.InvocationStarted, started.Phase);

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.CancelBeforeInvocation(ProjectId, "paid-1"));

        var budget = budgets.GetSnapshot(ProjectId);
        Assert.AreEqual(900L, budget.ReservedSpendMicros);
        Assert.AreEqual(1, budget.ReservedMeteredCalls);

        var duplicateAttempt = coordinator.Reserve(Estimate("paid-2", 200));
        Assert.IsFalse(duplicateAttempt.Allowed);
        Assert.AreEqual(AiBudgetDenialReason.SpendLimitExceeded, duplicateAttempt.DenialReason);
    }

    [TestMethod]
    public void MeteredReceipt_CannotCompleteBeforeInvocationStart()
    {
        var budgets = new AiProviderBudgetManager([Policy()]);
        var coordinator = new AiProviderInvocationCoordinator(budgets);
        coordinator.Reserve(Estimate("paid-1", 500));

        Assert.Throws<InvalidOperationException>(() => coordinator.Complete(
            new AiProviderUsageReceipt(ProjectId, "paid-1", "remote-premium", "USD", 450, 40, 30)));

        Assert.AreEqual(500L, budgets.GetSnapshot(ProjectId).ReservedSpendMicros);
        Assert.AreEqual(AiProviderInvocationPhase.Reserved, coordinator.GetSnapshot(ProjectId, "paid-1").Phase);
    }

    [TestMethod]
    public void PreInvocationCancellation_ReleasesBudgetAndLifecycleEntry()
    {
        var budgets = new AiProviderBudgetManager([Policy()]);
        var coordinator = new AiProviderInvocationCoordinator(budgets);
        coordinator.Reserve(Estimate("paid-1", 500));

        var released = coordinator.CancelBeforeInvocation(ProjectId, "paid-1");

        Assert.AreEqual(1_000L, released.RemainingSpendMicros);
        Assert.AreEqual(1, released.RemainingMeteredCalls);
        Assert.Throws<KeyNotFoundException>(() => coordinator.GetSnapshot(ProjectId, "paid-1"));
    }

    [TestMethod]
    public void StartedInvocation_CompletesIdempotentlyWithAuthoritativeReceipt()
    {
        var budgets = new AiProviderBudgetManager([Policy()]);
        var coordinator = new AiProviderInvocationCoordinator(budgets);
        coordinator.Reserve(Estimate("paid-1", 700));
        coordinator.MarkInvocationStarted(ProjectId, "paid-1");

        var receipt = new AiProviderUsageReceipt(ProjectId, "paid-1", "remote-premium", "USD", 650, 55, 35);
        var first = coordinator.Complete(receipt);
        var duplicate = coordinator.Complete(receipt);

        Assert.AreEqual(first, duplicate);
        Assert.AreEqual(650L, duplicate.CommittedSpendMicros);
        Assert.AreEqual(1, duplicate.CommittedMeteredCalls);
        Assert.AreEqual(AiProviderInvocationPhase.Completed, coordinator.GetSnapshot(ProjectId, "paid-1").Phase);
    }

    [TestMethod]
    public void InvocationLifecycle_IsProjectBound()
    {
        var otherProject = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var budgets = new AiProviderBudgetManager([Policy(), Policy(otherProject)]);
        var coordinator = new AiProviderInvocationCoordinator(budgets);
        coordinator.Reserve(Estimate("shared-id", 100));
        coordinator.Reserve(Estimate("shared-id", 100) with { ProjectId = otherProject });

        coordinator.MarkInvocationStarted(ProjectId, "shared-id");

        Assert.AreEqual(AiProviderInvocationPhase.InvocationStarted, coordinator.GetSnapshot(ProjectId, "shared-id").Phase);
        Assert.AreEqual(AiProviderInvocationPhase.Reserved, coordinator.GetSnapshot(otherProject, "shared-id").Phase);
    }

    private static AiProjectBudgetPolicy Policy(Guid? projectId = null) =>
        new(projectId ?? ProjectId, AiProviderBudgetProfile.Balanced, "USD", 1_000, 1, 1_000, 1_000);

    private static AiProviderCallEstimate Estimate(string requestId, long cost) =>
        new(
            ProjectId,
            requestId,
            "remote-premium",
            AiProviderLocation.Remote,
            AiProviderBillingMode.ExternalMetered,
            "USD",
            cost,
            100,
            100,
            500);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
