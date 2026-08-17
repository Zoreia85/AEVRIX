namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Deterministic baseline over the real QIR learning path. It measures only properties that are
/// exercised by the current implementation and intentionally leaves the global ACI incomplete.
/// </summary>
public static class QirCognitiveBaseline
{
    private const string SuiteId = "qir-cognitive-baseline-v1";

    public static IReadOnlyList<CognitiveMetricResult> Run()
    {
        var memoryIsolation = RunMemoryIsolation();
        var generalization = RunGeneralization();
        var safety = RunSafetyGovernance();
        return [memoryIsolation, generalization, safety];
    }

    private static CognitiveMetricResult RunMemoryIsolation()
    {
        var ledger = new QirLearningLedger();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        ledger.Record(Observation("obs-a", projectA, QirLearningSensitivity.ProjectConfidential, 0.95));
        ledger.Record(Observation("obs-b", projectB, QirLearningSensitivity.ProjectConfidential, 0.95));

        var a = ledger.ProjectSnapshot(projectA);
        var b = ledger.ProjectSnapshot(projectB);
        var success = a.Count == 1 && b.Count == 1 && a.All(x => x.ProjectId == projectA) && b.All(x => x.ProjectId == projectB);
        return Result(CognitiveMetric.MemoryRecall, success);
    }

    private static CognitiveMetricResult RunGeneralization()
    {
        var ledger = new QirLearningLedger();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        ledger.Record(Observation("obs-a", a, QirLearningSensitivity.Public, 0.94));
        ledger.Record(Observation("obs-b", b, QirLearningSensitivity.Public, 0.92));
        var promoted = ledger.Promote("dom-structure", FeatureHash());
        var advisor = new QirMissionHintAdvisor([
            new QirRoutingRule("dom-structure", MissionSpecialistKind.StaticStructure, 0.9, "validated-cross-project-pattern")
        ]);
        var hints = advisor.BuildHints([promoted]);
        var success = promoted.IndependentProjectCount == 2
            && hints.Count == 1
            && hints[0].Specialist == MissionSpecialistKind.StaticStructure
            && hints[0].PriorityScore > 0;
        return Result(CognitiveMetric.Generalization, success);
    }

    private static CognitiveMetricResult RunSafetyGovernance()
    {
        var ledger = new QirLearningLedger();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        ledger.Record(Observation("obs-a", a, QirLearningSensitivity.PersonalData, 0.99, containsPersonalData: true));
        ledger.Record(Observation("obs-b", b, QirLearningSensitivity.Public, 0.99));

        try
        {
            _ = ledger.Promote("dom-structure", FeatureHash());
            return Result(CognitiveMetric.SafetyGovernance, false);
        }
        catch (InvalidOperationException)
        {
            return Result(CognitiveMetric.SafetyGovernance, true);
        }
    }

    private static QirLearningObservation Observation(
        string id,
        Guid projectId,
        QirLearningSensitivity sensitivity,
        double confidence,
        bool containsPersonalData = false) =>
        new(
            id,
            projectId,
            "dom-structure",
            FeatureHash(),
            QirLearningBasis.ExperimentallyValidated,
            sensitivity,
            confidence,
            ["evidence-001"],
            DateTimeOffset.Parse("2026-08-17T00:00:00Z"),
            containsPersonalData,
            false);

    private static string FeatureHash() => new('a', 64);

    private static CognitiveMetricResult Result(CognitiveMetric metric, bool success) =>
        new(metric, 1, success ? 1 : 0, success ? 100 : 0, SuiteId);
}
