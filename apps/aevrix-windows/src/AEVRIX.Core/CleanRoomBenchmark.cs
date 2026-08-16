namespace Aevrix.Core;

public enum CleanRoomEvidenceKind
{
    PublicDocumentation,
    PublicUserInterface,
    AuthorizedRuntimeObservation,
    NetworkMetadata,
    PublicApiContract,
    OpenSourceReference
}

public enum CleanRoomRequirementClass
{
    Functional,
    Interoperability,
    Performance,
    Accessibility,
    Reliability,
    Security,
    DataModel
}

public sealed record CleanRoomEvidence(
    string Id,
    CleanRoomEvidenceKind Kind,
    Uri Source,
    string Observation,
    DateTimeOffset ObservedAt,
    string Sha256)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentNullException.ThrowIfNull(Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(Observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(Sha256);

        if (!Source.IsAbsoluteUri || Source.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Evidence source must be an absolute HTTP(S) URI.", nameof(Source));
        }

        if (ObservedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(ObservedAt));
        }

        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Evidence SHA-256 must contain exactly 64 hexadecimal characters.", nameof(Sha256));
        }
    }
}

public sealed record CleanRoomRequirement(
    string Id,
    CleanRoomRequirementClass Class,
    string Statement,
    IReadOnlyList<string> EvidenceIds,
    bool MustMatchBehavior,
    bool MustNotCopyExpression)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Statement);
        ArgumentNullException.ThrowIfNull(EvidenceIds);

        if (EvidenceIds.Count == 0)
        {
            throw new ArgumentException("Every derived requirement must cite at least one evidence item.", nameof(EvidenceIds));
        }

        if (!MustNotCopyExpression)
        {
            throw new InvalidOperationException("Clean-room requirements must explicitly prohibit copying protected expression.");
        }
    }
}

public sealed record CleanRoomImplementationAttestation(
    string ImplementationId,
    string Implementer,
    IReadOnlyList<string> RequirementIds,
    IReadOnlyList<string> SourceCodeInputs,
    bool HadDirectAccessToRestrictedImplementationArtifacts)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ImplementationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Implementer);
        ArgumentNullException.ThrowIfNull(RequirementIds);
        ArgumentNullException.ThrowIfNull(SourceCodeInputs);

        if (RequirementIds.Count == 0)
        {
            throw new ArgumentException("Implementation must identify the requirements it implements.", nameof(RequirementIds));
        }

        if (HadDirectAccessToRestrictedImplementationArtifacts)
        {
            throw new InvalidOperationException("A clean-room implementation cannot attest direct access to restricted implementation artifacts.");
        }
    }
}

public sealed record CleanRoomMetricResult(
    string Name,
    double Weight,
    double Score)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (Weight <= 0 || Weight > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Weight));
        }

        if (Score < 0 || Score > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Score));
        }
    }
}

public sealed record CleanRoomBenchmarkReport(
    double FunctionalEquivalence,
    IReadOnlyList<CleanRoomMetricResult> Metrics,
    IReadOnlyList<string> FailedRequirements,
    bool PassedIdentitySeparation,
    bool PassedRestrictedArtifactGuard)
{
    public bool Passed => FunctionalEquivalence >= 0.90
        && FailedRequirements.Count == 0
        && PassedIdentitySeparation
        && PassedRestrictedArtifactGuard;
}

public static class CleanRoomBenchmarkProtocol
{
    public static CleanRoomBenchmarkReport Evaluate(
        IReadOnlyCollection<CleanRoomEvidence> evidence,
        IReadOnlyCollection<CleanRoomRequirement> requirements,
        CleanRoomImplementationAttestation attestation,
        IReadOnlyCollection<CleanRoomMetricResult> metrics,
        IReadOnlyCollection<string> failedRequirements,
        bool passedIdentitySeparation,
        bool passedRestrictedArtifactGuard)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(failedRequirements);

        if (evidence.Count == 0 || requirements.Count == 0 || metrics.Count == 0)
        {
            throw new ArgumentException("Evidence, requirements, and metrics are mandatory.");
        }

        foreach (var item in evidence)
        {
            item.Validate();
        }

        foreach (var requirement in requirements)
        {
            requirement.Validate();
        }

        attestation.Validate();

        foreach (var metric in metrics)
        {
            metric.Validate();
        }

        EnsureUnique(evidence.Select(x => x.Id), "evidence");
        EnsureUnique(requirements.Select(x => x.Id), "requirement");
        EnsureUnique(metrics.Select(x => x.Name), "metric");

        var evidenceIds = evidence.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            if (requirement.EvidenceIds.Any(id => !evidenceIds.Contains(id)))
            {
                throw new InvalidOperationException($"Requirement '{requirement.Id}' cites unknown evidence.");
            }
        }

        var requirementIds = requirements.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (attestation.RequirementIds.Any(id => !requirementIds.Contains(id)))
        {
            throw new InvalidOperationException("Implementation attestation cites an unknown requirement.");
        }

        if (failedRequirements.Any(id => !requirementIds.Contains(id)))
        {
            throw new InvalidOperationException("Failed-requirement set contains an unknown requirement.");
        }

        var weightSum = metrics.Sum(x => x.Weight);
        if (Math.Abs(weightSum - 1.0) > 0.000001)
        {
            throw new InvalidOperationException("Benchmark metric weights must sum to exactly 1.0.");
        }

        var weightedScore = metrics.Sum(x => x.Weight * x.Score);
        return new CleanRoomBenchmarkReport(
            FunctionalEquivalence: weightedScore,
            Metrics: metrics.ToArray(),
            FailedRequirements: failedRequirements.ToArray(),
            PassedIdentitySeparation: passedIdentitySeparation,
            PassedRestrictedArtifactGuard: passedRestrictedArtifactGuard);
    }

    private static void EnsureUnique(IEnumerable<string> values, string kind)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!set.Add(value))
            {
                throw new InvalidOperationException($"Duplicate {kind} identifier '{value}'.");
            }
        }
    }
}
