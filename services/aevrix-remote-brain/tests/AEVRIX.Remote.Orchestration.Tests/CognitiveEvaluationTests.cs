using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class CognitiveEvaluationTests
{
    [TestMethod]
    public void PartialQirBaseline_DoesNotInventGlobalAci()
    {
        var metrics = QirCognitiveBaseline.Run();
        var report = new CognitiveEvaluationReport(
            "brain-baseline-v1",
            new string('a', 40),
            DateTimeOffset.Parse("2026-08-17T00:00:00Z"),
            metrics);

        report.Validate();

        Assert.AreEqual(3, metrics.Count);
        Assert.IsFalse(report.IsComplete);
        Assert.IsNull(report.Aci);
        Assert.AreEqual(100d, report.Find(CognitiveMetric.MemoryRecall)!.NormalizedScore);
        Assert.AreEqual(100d, report.Find(CognitiveMetric.Generalization)!.NormalizedScore);
        Assert.AreEqual(100d, report.Find(CognitiveMetric.SafetyGovernance)!.NormalizedScore);
    }

    [TestMethod]
    public void CompleteReport_ComputesTransparentEqualWeightAci()
    {
        var metrics = Enum.GetValues<CognitiveMetric>()
            .Select((metric, index) => new CognitiveMetricResult(metric, 10, 8, 80 + (index % 2) * 10, "synthetic-aggregation-test"))
            .ToArray();
        var report = new CognitiveEvaluationReport(
            "brain-aggregation-test",
            new string('b', 40),
            DateTimeOffset.Parse("2026-08-17T00:00:00Z"),
            metrics);

        Assert.IsTrue(report.IsComplete);
        Assert.AreEqual(85d, report.Aci);
    }

    [TestMethod]
    public void DuplicateMetric_IsRejected()
    {
        var metric = new CognitiveMetricResult(CognitiveMetric.MemoryRecall, 1, 1, 100, "duplicate-test");
        var report = new CognitiveEvaluationReport(
            "brain-duplicate-test",
            new string('c', 40),
            DateTimeOffset.Parse("2026-08-17T00:00:00Z"),
            [metric, metric]);

        Assert.Throws<InvalidDataException>(() => report.Validate());
    }
}
