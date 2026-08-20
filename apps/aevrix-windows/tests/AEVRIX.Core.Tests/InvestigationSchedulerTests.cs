using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class InvestigationSchedulerTests
{
    [TestMethod]
    public void Plan_RespectsConcurrencyAndPriorityForFreshWork()
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
            capacity,
            now);

        Assert.AreEqual(InvestigationRunState.Running, decisions.Single(item => item.InvestigationId == urgent).NextState);
        Assert.AreEqual(InvestigationRunState.Running, decisions.Single(item => item.InvestigationId == normal).NextState);
        Assert.AreEqual(InvestigationRunState.Queued, decisions.Single(item => item.InvestigationId == low).NextState);
        Assert.AreEqual(1, decisions.Single(item => item.InvestigationId == low).QueuePosition);
    }

    [TestMethod]
    public void Plan_AgedLowPriorityWorkEventuallyRunsAheadOfFreshUrgentArrival()
    {
        var capacity = new LocalCapacityRecommendation(
            LogicalProcessors: 4,
            AvailableMemoryBytes: 8L * 1024 * 1024 * 1024,
            RecommendedConcurrentInvestigations: 1,
            Rationale: "test");
        var now = new DateTimeOffset(2026, 8, 19, 21, 0, 0, TimeSpan.Zero);
        var agedLow = Guid.NewGuid();
        var freshUrgent = Guid.NewGuid();

        var decisions = InvestigationScheduler.Plan(
            [
                new InvestigationScheduleRequest(agedLow, InvestigationPriority.Low, now.AddHours(-4), InvestigationRunState.Queued),
                new InvestigationScheduleRequest(freshUrgent, InvestigationPriority.Urgent, now, InvestigationRunState.Ready)
            ],
            capacity,
            now);

        Assert.AreEqual(InvestigationRunState.Running, decisions.Single(item => item.InvestigationId == agedLow).NextState);
        Assert.AreEqual(InvestigationRunState.Queued, decisions.Single(item => item.InvestigationId == freshUrgent).NextState);
    }

    [TestMethod]
    public void Plan_PreservesAlreadyRunningWorkWithinCapacity()
    {
        var capacity = new LocalCapacityRecommendation(8, 16L * 1024 * 1024 * 1024, 1, "test");
        var now = DateTimeOffset.UtcNow;
        var running = Guid.NewGuid();
        var aged = Guid.NewGuid();

        var decisions = InvestigationScheduler.Plan(
            [
                new InvestigationScheduleRequest(running, InvestigationPriority.Low, now.AddHours(-1), InvestigationRunState.Running),
                new InvestigationScheduleRequest(aged, InvestigationPriority.Urgent, now.AddHours(-6), InvestigationRunState.Queued)
            ],
            capacity,
            now);

        Assert.AreEqual(InvestigationRunState.Running, decisions.Single(item => item.InvestigationId == running).NextState);
        Assert.AreEqual(InvestigationRunState.Queued, decisions.Single(item => item.InvestigationId == aged).NextState);
    }

    [TestMethod]
    public void Plan_PreservesAllRunningWorkWhenCapacityShrinksAndAdmitsNoNewWork()
    {
        var capacity = new LocalCapacityRecommendation(8, 16L * 1024 * 1024 * 1024, 2, "reduced");
        var now = DateTimeOffset.UtcNow;
        var runningA = Guid.NewGuid();
        var runningB = Guid.NewGuid();
        var runningC = Guid.NewGuid();
        var freshUrgent = Guid.NewGuid();

        var decisions = InvestigationScheduler.Plan(
            [
                new InvestigationScheduleRequest(runningA, InvestigationPriority.Low, now.AddHours(-3), InvestigationRunState.Running),
                new InvestigationScheduleRequest(runningB, InvestigationPriority.Normal, now.AddHours(-2), InvestigationRunState.Running),
                new InvestigationScheduleRequest(runningC, InvestigationPriority.High, now.AddHours(-1), InvestigationRunState.Running),
                new InvestigationScheduleRequest(freshUrgent, InvestigationPriority.Urgent, now, InvestigationRunState.Ready)
            ],
            capacity,
            now);

        Assert.AreEqual(3, decisions.Count(item => item.NextState == InvestigationRunState.Running));
        Assert.AreEqual(InvestigationRunState.Running, decisions.Single(item => item.InvestigationId == runningA).NextState);
        Assert.AreEqual(InvestigationRunState.Running, decisions.Single(item => item.InvestigationId == runningB).NextState);
        Assert.AreEqual(InvestigationRunState.Running, decisions.Single(item => item.InvestigationId == runningC).NextState);
        Assert.AreEqual(InvestigationRunState.Queued, decisions.Single(item => item.InvestigationId == freshUrgent).NextState);
        Assert.IsTrue(decisions.Single(item => item.InvestigationId == freshUrgent).Reason.Contains("acima da nova capacidade", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Plan_DoesNotAutoResumePausedOrBlockedWork()
    {
        var capacity = new LocalCapacityRecommendation(16, 32L * 1024 * 1024 * 1024, 4, "test");
        var paused = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var decisions = InvestigationScheduler.Plan(
            [
                new InvestigationScheduleRequest(paused, InvestigationPriority.Urgent, now, InvestigationRunState.Paused),
                new InvestigationScheduleRequest(blocked, InvestigationPriority.Urgent, now, InvestigationRunState.Blocked)
            ],
            capacity,
            now);

        Assert.AreEqual(InvestigationRunState.Paused, decisions.Single(item => item.InvestigationId == paused).NextState);
        Assert.AreEqual(InvestigationRunState.Blocked, decisions.Single(item => item.InvestigationId == blocked).NextState);
    }

    [TestMethod]
    public void Plan_ClampsExternallySuppliedCapacityToTenConcurrentInvestigations()
    {
        var capacity = new LocalCapacityRecommendation(128, 512L * 1024 * 1024 * 1024, 50, "external-test");
        var now = DateTimeOffset.UtcNow;
        var requests = Enumerable.Range(0, 12)
            .Select(index => new InvestigationScheduleRequest(
                Guid.NewGuid(),
                InvestigationPriority.Normal,
                now.AddMinutes(-index),
                InvestigationRunState.Ready))
            .ToArray();

        var decisions = InvestigationScheduler.Plan(requests, capacity, now);

        Assert.AreEqual(10, decisions.Count(item => item.NextState == InvestigationRunState.Running));
        Assert.AreEqual(2, decisions.Count(item => item.NextState == InvestigationRunState.Queued));
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
