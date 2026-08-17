namespace Aevrix.Remote.Orchestration;

public enum CognitiveMetric
{
    LogicalReasoning,
    MultiStepPlanning,
    MemoryRecall,
    ContextRecovery,
    ToolUse,
    GroundingFactuality,
    ConfidenceCalibration,
    Generalization,
    AdversarialRobustness,
    SafetyGovernance,
    Efficiency,
    EndToEndSuccess
}

public sealed record CognitiveMetricResult(
    CognitiveMetric Metric,
    int SampleCount,
    int SuccessCount,
    double NormalizedScore,
    string SuiteId)
{
    public CognitiveMetricResult Validate()
    {
        if (SampleCount < 1) throw new InvalidDataException("Cognitive metric sample count must be positive.");
        if (SuccessCount < 0 || SuccessCount > SampleCount) throw new InvalidDataException("Cognitive metric success count is invalid.");
        if (!double.IsFinite(NormalizedScore) || NormalizedScore is < 0 or > 100)
            throw new InvalidDataException("Cognitive metric score must be within [0,100].");
        QirLearningObservation.ValidateId(SuiteId, 3, 160);
        return this;
    }
}

public sealed record CognitiveEvaluationReport(
    string BrainVersion,
    string BrainCommitSha,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<CognitiveMetricResult> Metrics)
{
    private static readonly CognitiveMetric[] RequiredMetrics = Enum.GetValues<CognitiveMetric>();

    public CognitiveEvaluationReport Validate()
    {
        QirLearningObservation.ValidateId(BrainVersion, 1, 160);
        if (BrainCommitSha.Length != 40 || BrainCommitSha.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("Brain commit SHA must be 40 hexadecimal characters.");
        if (EvaluatedAt == default) throw new InvalidDataException("Cognitive evaluation timestamp is missing.");
        if (Metrics is null || Metrics.Count == 0) throw new InvalidDataException("Cognitive evaluation requires metrics.");
        if (Metrics.Any(metric => metric is null)) throw new InvalidDataException("Cognitive metric cannot be null.");
        foreach (var metric in Metrics) metric.Validate();
        if (Metrics.GroupBy(x => x.Metric).Any(group => group.Count() != 1))
            throw new InvalidDataException("Cognitive evaluation cannot contain duplicate metrics.");
        return this;
    }

    public bool IsComplete => RequiredMetrics.All(metric => Metrics.Any(x => x.Metric == metric));

    public double? Aci
    {
        get
        {
            Validate();
            if (!IsComplete) return null;
            return Math.Round(Metrics.Average(x => x.NormalizedScore), 2, MidpointRounding.AwayFromZero);
        }
    }

    public CognitiveMetricResult? Find(CognitiveMetric metric) => Metrics.SingleOrDefault(x => x.Metric == metric);
}
