namespace Aevrix.Core;

public enum InvestigationTargetKind
{
    DesktopApplication,
    MobileApplication,
    WebSystem,
    ApiService,
    Repository,
    EvidenceFiles,
    Other
}

public enum InvestigationStrategy
{
    Investigate,
    InvestigateAndEmulate,
    InvestigateAndBuildParallel,
    ReconstructWhiteLabel
}

public enum InvestigationRunState
{
    Draft,
    Ready,
    Queued,
    Running,
    Paused,
    Blocked,
    Failed,
    Completed,
    Cancelled
}

public enum InvestigationPhase
{
    IntakeAndAuthorization,
    Acquisition,
    StaticAnalysis,
    DynamicObservation,
    EvidenceCorrelation,
    BlueprintSynthesis,
    DifferentialValidation,
    Reconstruction,
    FinalQualityAssurance
}

public sealed record InvestigationInputArtifact(
    string DisplayName,
    string Path,
    long? SizeBytes = null,
    string? Sha256 = null);

public sealed record InvestigationDraft(
    Guid Id,
    string Workspace,
    InvestigationTargetKind TargetKind,
    InvestigationStrategy Strategy,
    string AuthorizationClass,
    string Target,
    string Goals,
    string Sensitivity,
    IReadOnlyList<InvestigationInputArtifact> Artifacts,
    DateTimeOffset CreatedAtUtc)
{
    public static InvestigationDraft Create(
        string workspace,
        InvestigationTargetKind targetKind,
        InvestigationStrategy strategy,
        string authorizationClass,
        string target,
        string goals,
        string sensitivity,
        IEnumerable<InvestigationInputArtifact>? artifacts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(goals);
        ArgumentException.ThrowIfNullOrWhiteSpace(sensitivity);

        var artifactList = artifacts?.ToArray() ?? Array.Empty<InvestigationInputArtifact>();
        if (RequiresExecutableArtifacts(targetKind) && artifactList.Length == 0)
        {
            throw new ArgumentException(
                "Executable application targets require at least one installer, binary or package artifact.",
                nameof(artifacts));
        }

        if (strategy is InvestigationStrategy.InvestigateAndEmulate && !SupportsEmulation(targetKind))
        {
            throw new ArgumentException(
                "The selected target class does not support the emulation strategy.",
                nameof(strategy));
        }

        return new InvestigationDraft(
            Guid.NewGuid(),
            workspace.Trim(),
            targetKind,
            strategy,
            authorizationClass.Trim(),
            target.Trim(),
            goals.Trim(),
            sensitivity.Trim(),
            artifactList,
            DateTimeOffset.UtcNow);
    }

    public static bool RequiresExecutableArtifacts(InvestigationTargetKind targetKind)
        => targetKind is InvestigationTargetKind.DesktopApplication or InvestigationTargetKind.MobileApplication;

    public static bool SupportsEmulation(InvestigationTargetKind targetKind)
        => targetKind is InvestigationTargetKind.DesktopApplication or InvestigationTargetKind.MobileApplication;
}

public sealed record InvestigationStageProgress(
    InvestigationPhase Phase,
    double Weight,
    double Completion)
{
    public InvestigationStageProgress Normalize()
        => this with
        {
            Weight = Math.Clamp(Weight, 0, 1000),
            Completion = Math.Clamp(Completion, 0, 1)
        };
}

public sealed record InvestigationProgressSnapshot(
    InvestigationRunState State,
    InvestigationPhase CurrentPhase,
    double PercentComplete,
    TimeSpan? EstimatedRemaining,
    DateTimeOffset SampledAtUtc,
    string? Blocker)
{
    public static InvestigationProgressSnapshot Create(
        InvestigationRunState state,
        InvestigationPhase currentPhase,
        IEnumerable<InvestigationStageProgress> stages,
        DateTimeOffset startedAtUtc,
        DateTimeOffset sampledAtUtc,
        string? blocker = null)
    {
        var normalized = stages.Select(stage => stage.Normalize()).ToArray();
        var totalWeight = normalized.Sum(stage => stage.Weight);
        var completedWeight = normalized.Sum(stage => stage.Weight * stage.Completion);
        var fraction = totalWeight <= 0 ? 0 : completedWeight / totalWeight;
        var percent = Math.Round(Math.Clamp(fraction * 100, 0, 100), 1);

        TimeSpan? eta = null;
        var elapsed = sampledAtUtc - startedAtUtc;
        if (state is InvestigationRunState.Running &&
            fraction >= 0.10 && fraction < 1.0 &&
            elapsed >= TimeSpan.FromMinutes(2))
        {
            var projectedTotalTicks = elapsed.Ticks / fraction;
            var remainingTicks = Math.Max(0, projectedTotalTicks - elapsed.Ticks);
            eta = TimeSpan.FromTicks((long)Math.Min(remainingTicks, TimeSpan.FromDays(30).Ticks));
        }

        return new InvestigationProgressSnapshot(
            state,
            currentPhase,
            percent,
            eta,
            sampledAtUtc,
            string.IsNullOrWhiteSpace(blocker) ? null : blocker.Trim());
    }
}

public sealed record LocalCapacityRecommendation(
    int LogicalProcessors,
    long AvailableMemoryBytes,
    int RecommendedConcurrentInvestigations,
    string Rationale)
{
    public static LocalCapacityRecommendation ForCurrentProcess()
    {
        var processors = Math.Max(1, Environment.ProcessorCount);
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (memory <= 0)
        {
            memory = 8L * 1024 * 1024 * 1024;
        }

        var cpuSlots = Math.Max(1, processors / 4);
        var memorySlots = Math.Max(1, (int)(memory / (6L * 1024 * 1024 * 1024)));
        var recommended = Math.Clamp(Math.Min(cpuSlots, memorySlots), 1, 8);
        var memoryGiB = memory / 1024d / 1024d / 1024d;

        return new LocalCapacityRecommendation(
            processors,
            memory,
            recommended,
            $"Estimativa conservadora baseada em {processors} processadores lógicos e {memoryGiB:F1} GiB de memória disponível. O orquestrador pode reduzir a concorrência sob pressão.");
    }
}
