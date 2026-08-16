namespace Aevrix.Core;

public enum EvidenceBasis
{
    Observed,
    ExperimentallyValidated,
    Inferred,
    VendorClaim
}

public enum ArchitectureElementKind
{
    User,
    Browser,
    Frontend,
    DesktopClient,
    MobileClient,
    ApiGateway,
    ApiService,
    Authentication,
    Worker,
    Queue,
    Storage,
    ExternalService,
    Unknown
}

public sealed record ConfidenceScore
{
    public ConfidenceScore(double value)
    {
        if (double.IsNaN(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Confidence must be between 0 and 1.");
        }

        Value = value;
    }

    public double Value { get; }
    public double Percent => Math.Round(Value * 100, 2);

    public static ConfidenceScore FromPercent(double percent) => new(percent / 100d);
}

public sealed record EvidenceReference(
    string Id,
    string Kind,
    string RelativePath,
    string Sha256,
    DateTimeOffset CapturedAt,
    EvidenceBasis Basis,
    string? Description = null)
{
    public EvidenceReference Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(RelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Sha256);

        if (Path.IsPathRooted(RelativePath) || RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        {
            throw new ArgumentException("Evidence paths must remain project-relative.", nameof(RelativePath));
        }

        if (Sha256.Length != 64 || Sha256.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new ArgumentException("Evidence SHA-256 must be 64 hexadecimal characters.", nameof(Sha256));
        }

        return this;
    }
}

public sealed record ArchitectureElement(
    string Id,
    string Name,
    ArchitectureElementKind Kind,
    EvidenceBasis Basis,
    ConfidenceScore Confidence,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record ArchitectureRelationship(
    string FromId,
    string ToId,
    string Relationship,
    EvidenceBasis Basis,
    ConfidenceScore Confidence,
    IReadOnlyList<string> EvidenceIds);

public sealed record WorkflowStep(
    string Id,
    string Label,
    string? RouteOrState,
    EvidenceBasis Basis,
    ConfidenceScore Confidence,
    IReadOnlyList<string> EvidenceIds);

public sealed record WorkflowModel(
    string Id,
    string Name,
    IReadOnlyList<WorkflowStep> Steps,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> Outcomes,
    ConfidenceScore Confidence);

public sealed record ApiEndpointModel(
    string Id,
    string Method,
    string PathTemplate,
    IReadOnlyList<string> RequestSchemaKeys,
    IReadOnlyList<string> ResponseSchemaKeys,
    IReadOnlyList<string> PaginationHints,
    EvidenceBasis Basis,
    ConfidenceScore Confidence,
    IReadOnlyList<string> EvidenceIds);

public sealed record UiComponentModel(
    string Id,
    string Name,
    string ComponentType,
    IReadOnlyList<string> States,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    EvidenceBasis Basis,
    ConfidenceScore Confidence,
    IReadOnlyList<string> EvidenceIds);

public sealed record BehavioralModel(
    string Id,
    string Name,
    string BehaviorUnderStudy,
    IReadOnlyList<string> InputVariables,
    IReadOnlyList<string> OutputVariables,
    int Experiments,
    int HoldoutCases,
    double HoldoutSimilarityPercent,
    ConfidenceScore Confidence,
    IReadOnlyList<string> Counterexamples,
    IReadOnlyList<string> EvidenceIds)
{
    public BehavioralModel Validate()
    {
        if (Experiments <= 0)
        {
            throw new InvalidOperationException("A promoted behavioral model requires at least one controlled experiment.");
        }

        if (HoldoutCases <= 0)
        {
            throw new InvalidOperationException("A promoted behavioral model requires at least one independent holdout case.");
        }

        if (HoldoutSimilarityPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(HoldoutSimilarityPercent));
        }

        return this;
    }
}

public sealed record ProjectBlueprint(
    int SchemaVersion,
    Guid ProjectId,
    string ProjectName,
    string TargetId,
    ProjectDomain Domain,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<EvidenceReference> Evidence,
    IReadOnlyList<ArchitectureElement> ArchitectureElements,
    IReadOnlyList<ArchitectureRelationship> ArchitectureRelationships,
    IReadOnlyList<WorkflowModel> Workflows,
    IReadOnlyList<ApiEndpointModel> ApiEndpoints,
    IReadOnlyList<UiComponentModel> UiComponents,
    IReadOnlyList<BehavioralModel> BehavioralModels,
    ReproductionReadiness Readiness,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> OpenQuestions)
{
    public const int CurrentSchemaVersion = 1;

    public ProjectBlueprint Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported Project Blueprint schema version {SchemaVersion}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetId);

        foreach (var reference in Evidence)
        {
            reference.Validate();
        }

        foreach (var model in BehavioralModels)
        {
            model.Validate();
        }

        var duplicateEvidence = Evidence
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEvidence is not null)
        {
            throw new InvalidOperationException($"Blueprint contains duplicate evidence id: {duplicateEvidence.Key}");
        }

        EnsureEvidenceBacked(ArchitectureElements.Select(item => (item.Id, item.EvidenceIds)), "architecture element");
        EnsureEvidenceBacked(ArchitectureRelationships.Select(item => ($"{item.FromId}->{item.ToId}", item.EvidenceIds)), "architecture relationship");
        EnsureEvidenceBacked(Workflows.SelectMany(workflow => workflow.Steps.Select(step => ($"{workflow.Id}/{step.Id}", step.EvidenceIds))), "workflow step");
        EnsureEvidenceBacked(ApiEndpoints.Select(item => (item.Id, item.EvidenceIds)), "API endpoint");
        EnsureEvidenceBacked(UiComponents.Select(item => (item.Id, item.EvidenceIds)), "UI component");
        EnsureEvidenceBacked(BehavioralModels.Select(item => (item.Id, item.EvidenceIds)), "behavioral model");

        var architectureIds = ArchitectureElements.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var danglingRelationship = ArchitectureRelationships.FirstOrDefault(item => !architectureIds.Contains(item.FromId) || !architectureIds.Contains(item.ToId));
        if (danglingRelationship is not null)
        {
            throw new InvalidOperationException($"Blueprint relationship references an unknown architecture element: {danglingRelationship.FromId}->{danglingRelationship.ToId}");
        }

        var evidenceIds = Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var referencedEvidence = ArchitectureElements.SelectMany(item => item.EvidenceIds)
            .Concat(ArchitectureRelationships.SelectMany(item => item.EvidenceIds))
            .Concat(Workflows.SelectMany(workflow => workflow.Steps.SelectMany(step => step.EvidenceIds)))
            .Concat(ApiEndpoints.SelectMany(item => item.EvidenceIds))
            .Concat(UiComponents.SelectMany(item => item.EvidenceIds))
            .Concat(BehavioralModels.SelectMany(item => item.EvidenceIds));

        var missing = referencedEvidence.Where(id => !evidenceIds.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Blueprint references missing evidence ids: {string.Join(", ", missing)}");
        }

        return this;
    }

    private static void EnsureEvidenceBacked(
        IEnumerable<(string Id, IReadOnlyList<string> EvidenceIds)> items,
        string kind)
    {
        foreach (var item in items)
        {
            if (item.EvidenceIds is null || item.EvidenceIds.Count == 0)
            {
                throw new InvalidOperationException($"Blueprint {kind} {item.Id} has no evidence reference.");
            }
        }
    }

}
