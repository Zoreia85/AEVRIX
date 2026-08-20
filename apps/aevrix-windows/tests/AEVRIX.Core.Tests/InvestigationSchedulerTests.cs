using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class InvestigationSchedulerTests
{
    [TestMethod]
    public void Plan_RespectsConcurrencyAndPriority()
    {
        var capacity = new LocalCapacityRecommendation(
            LogicalProcessors: 8,
            AvailableMemoryBytes: 16L * 1024 * 1024 * 1024,
            RecommendedConcurrentInvestigations: 2,
            Rationale: "test");
        var now = DateTimeOffset.UtcNow;
        var low = Guid.NewGuid();
        var urgent = Guid.NewGuid();
        var normal = Guid.NewGuid();

        var decisions = InvestigationScheduler.Plan(
            [
                new InvestigationScheduleRequest(low, InvestigationPriority.Low, now.AddMinutes(-3), InvestigationRunState.Ready),
                new InvestigationScheduleRequest(urgent, InvestigationPriority.Urgent, now.AddMinutes(-1), InvestigationRunState.Ready),
                new InvestigationScheduleRequest(normal, InvestigationPriority.Normal, now.AddMinutes(-2), InvestigationRunState.Ready)
            ],
            capacity);

        Assert.AreEqual(InvestigationRunState.Running, decisions.Single(item => item.InvestigationId == urgent).NextState);
        Assert.AreEqual(InvestigationRunState.Running, decisions.Single(item => item.InvestigationId == normal).NextState);
        Assert.AreEqual(InvestigationRunState.Queued, decisions.Single(item => item.InvestigationId == low).NextState);
        Assert.AreEqual(1, decisions.Single(item => item.InvestigationId == low).QueuePosition);
    }

    [TestMethod]
    public void Plan_DoesNotAutoResumePausedOrBlockedWork()
    {
        var capacity = new LocalCapacityRecommendation(16, 32L * 1024 * 1024 * 1024, 4, "test");
        var paused = Guid.NewGuid();
        var blocked = Guid.NewGuid();

        var decisions = InvestigationScheduler.Plan(
            [
                new InvestigationScheduleRequest(paused, InvestigationPriority.Urgent, DateTimeOffset.UtcNow, InvestigationRunState.Paused),
                new InvestigationScheduleRequest(blocked, InvestigationPriority.Urgent, DateTimeOffset.UtcNow, InvestigationRunState.Blocked)
            ],
            capacity);

        Assert.AreEqual(InvestigationRunState.Paused, decisions.Single(item => item.InvestigationId == paused).NextState);
        Assert.AreEqual(InvestigationRunState.Blocked, decisions.Single(item => item.InvestigationId == blocked).NextState);
    }

    [TestMethod]
    public void StateMachine_RejectsCompletedToRunning()
    {
        Assert.IsFalse(InvestigationStateMachine.CanTransition(
            InvestigationRunState.Completed,
            InvestigationRunState.Running));
        Assert.Throws<InvalidOperationException>(() => InvestigationStateMachine.RequireTransition(
            InvestigationRunState.Completed,
            InvestigationRunState.Running));
    }

    [TestMethod]
    public void ResourceBudget_IsConservativeAndBounded()
    {
        var capacity = new LocalCapacityRecommendation(
            LogicalProcessors: 24,
            AvailableMemoryBytes: 48L * 1024 * 1024 * 1024,
            RecommendedConcurrentInvestigations: 6,
            Rationale: "test");

        var budget = InvestigationResourceBudget.ConservativeDefault(capacity);

        Assert.IsTrue(budget.CpuWeight >= 1);
        Assert.IsTrue(budget.MemoryBytes >= 1024L * 1024 * 1024);
        Assert.IsTrue(budget.MaxParallelAgentPackages >= 1);
        Assert.IsTrue(budget.MaxParallelAgentPackages <= 4);
    }
}
