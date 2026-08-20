using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class InvestigationAgentPlanTests
{
    [TestMethod]
    public void ParallelBuildPlan_ContainsInvestigationBuilderJudgeAndQa()
    {
        var plan = InvestigationAgentPlan.Create(
            InvestigationStrategy.InvestigateAndBuildParallel,
            InvestigationTargetKind.DesktopApplication);

        Assert.IsTrue(plan.WorkPackages.Any(item => item.Role == InvestigationAgentRole.StaticAnalyzer));
        Assert.IsTrue(plan.WorkPackages.Any(item => item.Role == InvestigationAgentRole.DynamicObserver));
        Assert.IsTrue(plan.WorkPackages.Any(item => item.Role == InvestigationAgentRole.CleanRoomBuilder));
        Assert.IsTrue(plan.WorkPackages.Any(item => item.Role == InvestigationAgentRole.DifferentialJudge));
        Assert.IsTrue(plan.WorkPackages.Any(item => item.Role == InvestigationAgentRole.QualityAssurance));
    }

    [TestMethod]
    public void Builder_DoesNotBecomeReadyUntilEvidenceDependencyIsVerifiedAndBound()
    {
        var plan = InvestigationAgentPlan.Create(
            InvestigationStrategy.InvestigateAndBuildParallel,
            InvestigationTargetKind.DesktopApplication);
        var states = plan.WorkPackages.ToDictionary(item => item.Id, item => item.State, StringComparer.Ordinal);
        states["coordination"] = AgentWorkPackageState.Verified;
        states["acquisition"] = AgentWorkPackageState.Verified;
        states["static-analysis"] = AgentWorkPackageState.Verified;
        states["dynamic-observation"] = AgentWorkPackageState.Verified;
        states["evidence-verification"] = AgentWorkPackageState.Verified;

        var withoutBinding = plan.GetReadyPackages(states, new HashSet<string>(StringComparer.Ordinal));
        Assert.IsFalse(withoutBinding.Any(item => item.Id == "clean-room-build"));

        var withBinding = plan.GetReadyPackages(
            states,
            new HashSet<string>(new[] { "evidence-verification" }, StringComparer.Ordinal));
        Assert.IsTrue(withBinding.Any(item => item.Id == "clean-room-build"));
    }

    [TestMethod]
    public void Emulation_IsRejectedForWebTarget()
    {
        Assert.Throws<ArgumentException>(() => InvestigationAgentPlan.Create(
            InvestigationStrategy.InvestigateAndEmulate,
            InvestigationTargetKind.WebSystem));
    }
}
