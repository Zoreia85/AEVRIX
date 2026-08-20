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

public sealed record InvestigationProgressEvidenceSample(
    DateTimeOffset SampledAtUtc,
    double PercentComplete,
    string EvidenceId)
{
    public void Validate(DateTimeOffset startedAtUtc, DateTimeOffset currentSampledAtUtc)
    {
        WorkspaceScope.ValidateToken(EvidenceId, nameof(EvidenceId));
        if (!double.IsFinite(PercentComplete) || PercentComplete < 0 || PercentComplete > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(PercentComplete), "Progress evidence must be between 0 and 100 percent.");
        }
        if (SampledAtUtc < startedAtUtc || SampledAtUtc > currentSampledAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(SampledAtUtc), "Progress evidence timestamp must fall within the investigation execution window.");
        }
    }
}

public sealed record InvestigationProgressSnapshot(
    InvestigationRunState State,
    InvestigationPhase CurrentPhase,
    double PercentComplete,
    TimeSpan? EstimatedRemaining,
    DateTimeOffset SampledAtUtc,
    string? Blocker)
{
    private const int MinimumEtaEvidenceSamples = 3;
    private const double MinimumEtaProgressDelta = 5.0;

    public static InvestigationProgressSnapshot Create(
        InvestigationRunState state,
        InvestigationPhase currentPhase,
        IEnumerable<InvestigationStageProgress> stages,
        DateTimeOffset startedAtUtc,
        DateTimeOffset sampledAtUtc,
        string? blocker = null,
        IEnumerable<InvestigationProgressEvidenceSample>? executionHistory = null)
    {
        if (sampledAtUtc < startedAtUtc)
        {
            throw new ArgumentException("Progress sample time cannot precede the investigation start time.", nameof(sampledAtUtc));
        }

        var normalized = stages.Select(stage => stage.Normalize()).ToArray();
        var totalWeight = normalized.Sum(stage => stage.Weight);
        var completedWeight = normalized.Sum(stage => stage.Weight * stage.Completion);
        var fraction = totalWeight <= 0 ? 0 : completedWeight / totalWeight;
        var percent = Math.Round(Math.Clamp(fraction * 100, 0, 100), 1);
        var eta = EstimateRemainingFromEvidence(
            state,
            percent,
            startedAtUtc,
            sampledAtUtc,
            executionHistory);

        return new InvestigationProgressSnapshot(
            state,
            currentPhase,
            percent,
            eta,
            sampledAtUtc,
            string.IsNullOrWhiteSpace(blocker) ? null : blocker.Trim());
    }

    private static TimeSpan? EstimateRemainingFromEvidence(
        InvestigationRunState state,
        double currentPercent,
        DateTimeOffset startedAtUtc,
        DateTimeOffset sampledAtUtc,
        IEnumerable<InvestigationProgressEvidenceSample>? executionHistory)
    {
        if (state is not InvestigationRunState.Running ||
            currentPercent < 10 ||
            currentPercent >= 100 ||
            executionHistory is null)
        {
            return null;
        }

        var samples = executionHistory
            .OrderBy(sample => sample.SampledAtUtc)
            .ToArray();
        if (samples.Length < MinimumEtaEvidenceSamples)
        {
            return null;
        }

        foreach (var sample in samples)
        {
            sample.Validate(startedAtUtc, sampledAtUtc);
        }

        if (samples.Select(sample => sample.EvidenceId).Distinct(StringComparer.Ordinal).Count() != samples.Length)
        {
            return null;
        }

        for (var index = 1; index < samples.Length; index++)
        {
            if (samples[index].SampledAtUtc <= samples[index - 1].SampledAtUtc ||
                samples[index].PercentComplete < samples[index - 1].PercentComplete)
            {
                return null;
            }
        }

        var first = samples[0];
        var last = samples[^1];
        if (last.SampledAtUtc != sampledAtUtc || Math.Abs(last.PercentComplete - currentPercent) > 0.05)
        {
            return null;
        }

        var observationWindow = last.SampledAtUtc - first.SampledAtUtc;
        var progressDelta = last.PercentComplete - first.PercentComplete;
        if (observationWindow < TimeSpan.FromMinutes(2) || progressDelta < MinimumEtaProgressDelta)
        {
            return null;
        }

        var percentPerSecond = progressDelta / observationWindow.TotalSeconds;
        if (!double.IsFinite(percentPerSecond) || percentPerSecond <= 0)
        {
            return null;
        }

        var remainingSeconds = (100 - currentPercent) / percentPerSecond;
        if (!double.IsFinite(remainingSeconds) || remainingSeconds < 0)
        {
            return null;
        }

        var boundedSeconds = Math.Min(remainingSeconds, TimeSpan.FromDays(30).TotalSeconds);
        return TimeSpan.FromSeconds(boundedSeconds);
    }
}

public sealed record LocalCapacityRecommendation(
    int LogicalProcessors,
    long AvailableMemoryBytes,
    int RecommendedConcurrentInvestigations,
    string Rationale)
{
    public const int ProductMaximumConcurrentInvestigations = 10;

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
        var recommended = Math.Clamp(
            Math.Min(cpuSlots, memorySlots),
            1,
            ProductMaximumConcurrentInvestigations);
        var memoryGiB = memory / 1024d / 1024d / 1024d;

        return new LocalCapacityRecommendation(
            processors,
            memory,
            recommended,
            $"Estimativa conservadora baseada em {processors} processadores lógicos e {memoryGiB:F1} GiB de memória disponível. O produto aceita até {ProductMaximumConcurrentInvestigations} investigações concorrentes, mas o orquestrador pode reduzir a concorrência sob pressão.");
    }
}
