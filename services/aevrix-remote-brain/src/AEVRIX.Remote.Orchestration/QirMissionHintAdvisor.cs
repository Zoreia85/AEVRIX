namespace Aevrix.Remote.Orchestration;

public sealed record QirRoutingRule(
    string PatternKey,
    MissionSpecialistKind Specialist,
    double Weight,
    string RationaleCode)
{
    public QirRoutingRule Validate()
    {
        QirLearningObservation.ValidateId(PatternKey, 3, 160);
        if (!double.IsFinite(Weight) || Weight is <= 0 or > 1)
        {
            throw new InvalidDataException("QIR routing weight must be within (0,1].");
        }
        if (!QirLearningObservation.IsSafeId(RationaleCode, 3, 96))
        {
            throw new InvalidDataException("QIR routing rationale code is invalid.");
        }
        return this;
    }
}

public sealed record QirMissionHintPolicy(
    int MinimumIndependentProjects = 2,
    double MinimumPatternConfidence = 0.85,
    int MaximumHints = 8)
{
    public QirMissionHintPolicy Validate()
    {
        if (MinimumIndependentProjects is < 2 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumIndependentProjects));
        }
        if (!double.IsFinite(MinimumPatternConfidence) || MinimumPatternConfidence is < 0.5 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumPatternConfidence));
        }
        if (MaximumHints is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumHints));
        }
        return this;
    }
}

public sealed record QirMissionHint(
    MissionSpecialistKind Specialist,
    double PriorityScore,
    string RationaleCode,
    IReadOnlyList<string> PatternIds)
{
    public bool IsEvidence => false;
    public bool CanSatisfyEvidenceRequirement => false;
    public bool CanDriveBlueprint => false;
}

/// <summary>
/// Converts sanitized global QIR patterns into bounded routing hints only.
/// Hints never carry project ids, evidence ids, raw observations or claims and therefore
/// cannot satisfy evidence requirements or drive blueprint reconstruction.
/// </summary>
public sealed class QirMissionHintAdvisor
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<QirRoutingRule>> _rules;
    private readonly QirMissionHintPolicy _policy;

    public QirMissionHintAdvisor(
        IEnumerable<QirRoutingRule> rules,
        QirMissionHintPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _policy = (policy ?? new QirMissionHintPolicy()).Validate();

        var validated = rules.Select(rule => (rule ?? throw new InvalidDataException("QIR routing rule cannot be null.")).Validate()).ToArray();
        _rules = validated
            .GroupBy(rule => rule.PatternKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<QirRoutingRule>)group
                    .OrderByDescending(rule => rule.Weight)
                    .ThenBy(rule => rule.Specialist)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    public IReadOnlyList<QirMissionHint> BuildHints(IReadOnlyCollection<QirGlobalPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count > 1024)
        {
            throw new InvalidDataException("QIR hint input exceeds safe bounds.");
        }

        var contributions = new List<(QirRoutingRule Rule, QirGlobalPattern Pattern, double Score)>();
        foreach (var pattern in patterns)
        {
            ValidateGlobalPattern(pattern);
            if (pattern.IndependentProjectCount < _policy.MinimumIndependentProjects
                || pattern.Confidence < _policy.MinimumPatternConfidence
                || !_rules.TryGetValue(pattern.PatternKey, out var matchingRules))
            {
                continue;
            }

            foreach (var rule in matchingRules)
            {
                contributions.Add((rule, pattern, Math.Clamp(rule.Weight * pattern.Confidence, 0, 1)));
            }
        }

        return contributions
            .GroupBy(item => item.Rule.Specialist)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(item => item.Score)
                    .ThenBy(item => item.Pattern.PatternId, StringComparer.Ordinal)
                    .ToArray();
                var best = ordered[0];
                return new QirMissionHint(
                    group.Key,
                    best.Score,
                    best.Rule.RationaleCode,
                    ordered.Select(item => item.Pattern.PatternId)
                        .Distinct(StringComparer.Ordinal)
                        .Take(32)
                        .ToArray());
            })
            .OrderByDescending(hint => hint.PriorityScore)
            .ThenBy(hint => hint.Specialist)
            .Take(_policy.MaximumHints)
            .ToArray();
    }

    private static void ValidateGlobalPattern(QirGlobalPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        QirLearningObservation.ValidateId(pattern.PatternId, 3, 160);
        QirLearningObservation.ValidateId(pattern.PatternKey, 3, 160);
        if (pattern.FeatureHash.Length != 64 || pattern.FeatureHash.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException("QIR global pattern feature hash is invalid.");
        }
        if (pattern.IndependentProjectCount < 1 || pattern.ObservationCount < pattern.IndependentProjectCount)
        {
            throw new InvalidDataException("QIR global pattern support counts are invalid.");
        }
        if (!double.IsFinite(pattern.Confidence) || pattern.Confidence is < 0 or > 1)
        {
            throw new InvalidDataException("QIR global pattern confidence is invalid.");
        }
        if (pattern.PromotedAt == default)
        {
            throw new InvalidDataException("QIR global pattern promotion timestamp is missing.");
        }
    }
}
