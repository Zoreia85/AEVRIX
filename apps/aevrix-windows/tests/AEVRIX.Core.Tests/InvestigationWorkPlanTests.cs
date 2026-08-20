using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class InvestigationWorkPlanTests
{
    [TestMethod]
    public void Create_DesktopApplicationRequiresArtifact()
    {
        Assert.Throws<ArgumentException>(() => InvestigationDraft.Create(
            "workspace",
            InvestigationTargetKind.DesktopApplication,
            InvestigationStrategy.Investigate,
            "owned",
            "Example Desktop App",
            "Map behavior",
            "standard"));
    }

    [TestMethod]
    public void Create_WebSystemDoesNotRequireLocalArtifact()
    {
        var draft = InvestigationDraft.Create(
            "workspace",
            InvestigationTargetKind.WebSystem,
            InvestigationStrategy.Investigate,
            "authorized",
            "https://example.test",
            "Map authorized behavior",
            "standard");

        Assert.AreEqual(InvestigationTargetKind.WebSystem, draft.TargetKind);
        Assert.AreEqual(0, draft.Artifacts.Count);
    }

    [TestMethod]
    public void Create_RejectsEmulationForNonExecutableTarget()
    {
        Assert.Throws<ArgumentException>(() => InvestigationDraft.Create(
            "workspace",
            InvestigationTargetKind.WebSystem,
            InvestigationStrategy.InvestigateAndEmulate,
            "authorized",
            "https://example.test",
            "Observe",
            "standard"));
    }

    [TestMethod]
    public void Progress_UsesWeightedVerifiedWork()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var snapshot = InvestigationProgressSnapshot.Create(
            InvestigationRunState.Running,
            InvestigationPhase.StaticAnalysis,
            [
                new InvestigationStageProgress(InvestigationPhase.IntakeAndAuthorization, 10, 1),
                new InvestigationStageProgress(InvestigationPhase.Acquisition, 20, 1),
                new InvestigationStageProgress(InvestigationPhase.StaticAnalysis, 30, 0.5),
                new InvestigationStageProgress(InvestigationPhase.BlueprintSynthesis, 40, 0)
            ],
            started,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(45.0, snapshot.PercentComplete);
        Assert.IsNotNull(snapshot.EstimatedRemaining);
    }

    [TestMethod]
    public void Progress_DoesNotInventEtaBeforeEnoughEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = InvestigationProgressSnapshot.Create(
            InvestigationRunState.Running,
            InvestigationPhase.Acquisition,
            [new InvestigationStageProgress(InvestigationPhase.Acquisition, 100, 0.05)],
            now.AddSeconds(-30),
            now);

        Assert.AreEqual(5.0, snapshot.PercentComplete);
        Assert.IsNull(snapshot.EstimatedRemaining);
    }

    [TestMethod]
    public void Capacity_IsAlwaysBoundedAndConservative()
    {
        var capacity = LocalCapacityRecommendation.ForCurrentProcess();

        Assert.IsTrue(capacity.RecommendedConcurrentInvestigations >= 1);
        Assert.IsTrue(capacity.RecommendedConcurrentInvestigations <= LocalCapacityRecommendation.ProductMaximumConcurrentInvestigations);
        Assert.AreEqual(10, LocalCapacityRecommendation.ProductMaximumConcurrentInvestigations);
        Assert.IsTrue(capacity.LogicalProcessors >= 1);
    }
}
