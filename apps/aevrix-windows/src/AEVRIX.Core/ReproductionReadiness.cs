namespace Aevrix.Core;

public sealed record ReadinessDimension(string Name, double Percent, double Weight)
{
    public ReadinessDimension Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (Percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Percent));
        }

        if (Weight is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Weight));
        }

        return this;
    }
}

public sealed record ReproductionReadiness(
    double OverallPercent,
    string Grade,
    bool ReadyForIndependentRebuild,
    IReadOnlyList<ReadinessDimension> Dimensions,
    IReadOnlyList<string> BlockingReasons)
{
    public static ReproductionReadiness Calculate(
        double structuralCoverage,
        double workflowCoverage,
        double dataApiCoverage,
        double uiCoverage,
        double behavioralCoverage,
        double evidenceConfidence,
        int unresolvedCriticalQuestions,
        bool hasUnresolvedSessionInterruptions,
        bool hasOpenPagination,
        bool hasMaterialEvidenceIntegrityFailure)
    {
        var dimensions = new[]
        {
            new ReadinessDimension("Structural Coverage", structuralCoverage, 0.24).Validate(),
            new ReadinessDimension("Workflow Coverage", workflowCoverage, 0.20).Validate(),
            new ReadinessDimension("Data/API Coverage", dataApiCoverage, 0.18).Validate(),
            new ReadinessDimension("UI Observable Coverage", uiCoverage, 0.10).Validate(),
            new ReadinessDimension("Behavioral Similarity", behavioralCoverage, 0.20).Validate(),
            new ReadinessDimension("Evidence Confidence", evidenceConfidence, 0.08).Validate()
        };

        var totalWeight = dimensions.Sum(item => item.Weight);
        var weighted = dimensions.Sum(item => item.Percent * item.Weight) / totalWeight;
        var blockers = new List<string>();

        if (hasMaterialEvidenceIntegrityFailure)
        {
            blockers.Add("Evidence integrity failure must be resolved before independent reconstruction.");
            weighted = Math.Min(weighted, 39.99);
        }

        if (hasUnresolvedSessionInterruptions)
        {
            blockers.Add("One or more authenticated capture sessions ended without verified recovery.");
            weighted = Math.Min(weighted, 79.99);
        }

        if (hasOpenPagination)
        {
            blockers.Add("One or more discovered paginated surfaces remain unexplored.");
            weighted = Math.Min(weighted, 84.99);
        }

        if (unresolvedCriticalQuestions > 0)
        {
            blockers.Add($"{unresolvedCriticalQuestions} critical architecture/behavior questions remain unresolved.");
            weighted = Math.Min(weighted, unresolvedCriticalQuestions >= 5 ? 74.99 : 89.99);
        }

        var overall = Math.Round(Math.Clamp(weighted, 0, 100), 2);
        var grade = overall switch
        {
            >= 95 => "A+",
            >= 90 => "A",
            >= 85 => "B+",
            >= 80 => "B",
            >= 70 => "C",
            >= 60 => "D",
            _ => "E"
        };

        var ready = overall >= 90
            && blockers.Count == 0
            && structuralCoverage >= 90
            && workflowCoverage >= 85
            && evidenceConfidence >= 90;

        return new ReproductionReadiness(overall, grade, ready, dimensions, blockers);
    }
}
