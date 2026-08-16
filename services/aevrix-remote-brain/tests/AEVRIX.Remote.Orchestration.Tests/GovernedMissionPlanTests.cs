using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class GovernedMissionPlanTests
{
    [TestMethod]
    public async Task DeterministicPlannerCreatesGovernedPlanWithoutQir()
    {
        var plan = BuildPlan();
        var request = new MissionPlanningRequest(
            plan,
            Scope(plan),
            [Authority("task-a")],
            []);

        var governed = await new DeterministicMissionPlanner().CreateAsync(request);

        Assert.AreEqual(plan.MissionId, governed.ExecutionPlan.MissionId);
        Assert.AreEqual(0, governed.PlanningHints.Count);
    }

    [TestMethod]
    public async Task PlannerRejectsCrossScopeTarget()
    {
        var plan = BuildPlan();
        var scope = Scope(plan) with { TargetId = "other-target" };
        var request = new MissionPlanningRequest(plan, scope, [Authority("task-a")], []);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new DeterministicMissionPlanner().CreateAsync(request));
    }

    [TestMethod]
    public async Task PlannerRejectsMissingTaskAuthorityCoverage()
    {
        var plan = BuildPlan();
        var request = new MissionPlanningRequest(plan, Scope(plan), [], []);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new DeterministicMissionPlanner().CreateAsync(request));
    }

    [TestMethod]
    public async Task ThirdPartyCleanRoomRejectsDynamicInstrumentation()
    {
        var plan = BuildPlan();
        var authority = Authority("task-a") with
        {
            Authority = MissionRuntimeAuthority.ReadProjectWorkspace
                | MissionRuntimeAuthority.DynamicInstrumentation
        };
        var request = new MissionPlanningRequest(plan, Scope(plan), [authority], []);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new DeterministicMissionPlanner().CreateAsync(request));
    }

    [TestMethod]
    public void ControlPlaneEnforcesCanonicalProgressionAndDigestChain()
    {
        var execution = BuildPlan();
        var plan = new GovernedMissionPlan(
            execution,
            Scope(execution),
            [Authority("task-a")],
            []).Validate();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var current = MissionControlSnapshot.CreateInitial(plan, t0);
        current.Verify();

        foreach (var phase in new[]
        {
            MissionControlPhase.Authorized,
            MissionControlPhase.Executing,
            MissionControlPhase.Evaluating,
            MissionControlPhase.Judged,
            MissionControlPhase.Admitted,
            MissionControlPhase.BlueprintEligible
        })
        {
            var previous = current;
            current = MissionControlStateMachine.Advance(current, phase, current.ObservedAtUtc.AddSeconds(1), phase.ToString());
            current.Verify();
            Assert.AreEqual(previous.StateDigestSha256, current.PreviousStateDigestSha256);
            Assert.AreEqual(previous.Sequence + 1, current.Sequence);
        }
    }

    [TestMethod]
    public void ControlPlaneRejectsSkippedPhaseAndClockRollback()
    {
        var execution = BuildPlan();
        var governed = new GovernedMissionPlan(execution, Scope(execution), [Authority("task-a")], []).Validate();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var initial = MissionControlSnapshot.CreateInitial(governed, t0);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MissionControlStateMachine.Advance(initial, MissionControlPhase.Executing, t0.AddSeconds(1), "skip"));

        var authorized = MissionControlStateMachine.Advance(
            initial, MissionControlPhase.Authorized, t0.AddSeconds(1), "authorized");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MissionControlStateMachine.Advance(authorized, MissionControlPhase.Executing, t0, "rollback"));
    }

    private static MissionPlan BuildPlan()
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        return new MissionPlan(
            "mission-a",
            projectId,
            "target-a",
            [new MissionTaskSpec(
                "task-a",
                MissionSpecialistKind.StaticAnalysis,
                "Inspect the authorized artifact.",
                ["evidence-a"],
                [])]);
    }

    private static MissionScope Scope(MissionPlan plan) =>
        new("workspace-a", "subject-a", plan.ProjectId, plan.TargetId, MissionAuthorizationClass.ThirdPartyCleanRoom);

    private static MissionTaskAuthority Authority(string taskId) =>
        new(
            taskId,
            MissionRuntimeAuthority.ReadProjectWorkspace,
            [],
            268_435_456,
            TimeSpan.FromMinutes(10));
}
