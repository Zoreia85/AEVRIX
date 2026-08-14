namespace Aevrix.Remote.Orchestration;

public sealed class EvidenceFusionModelProvider : IAevrixModelProvider
{
    public const string ClaimKeyContext = "fusion-claim-key";
    private readonly EvidenceBus _bus;
    private readonly EvidenceFusionEngine _fusion;

    public EvidenceFusionModelProvider(EvidenceBus bus, EvidenceFusionEngine fusion)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _fusion = fusion ?? throw new ArgumentNullException(nameof(fusion));
    }

    public string ProviderId => "evidence-fusion";

    public Task<ModelAnalysisCandidate> AnalyzeAsync(AnalysisTask task, CancellationToken cancellationToken = default)
    {
        task.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!task.Context.TryGetValue(ClaimKeyContext, out var claimKey) || !MissionTaskSpec.IsSafeId(claimKey, 3, 160))
            throw new InvalidDataException("Fusion analysis task is missing a governed claim key.");

        var allowed = task.EvidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observations = _bus.Snapshot(task.ProjectId, task.TargetId)
            .Where(item => string.Equals(item.ClaimKey, claimKey, StringComparison.OrdinalIgnoreCase) && allowed.Contains(item.EvidenceId))
            .ToArray();
        if (observations.Length == 0)
            throw new InvalidOperationException("No governed evidence observations are available for the requested claim.");

        var fused = _fusion.Fuse(task.ProjectId, task.TargetId, claimKey, observations);
        var risk = fused.State switch
        {
            EvidenceFusionState.Convergent => ModelRiskLevel.Low,
            EvidenceFusionState.Insufficient => ModelRiskLevel.Medium,
            EvidenceFusionState.Contested => ModelRiskLevel.High,
            _ => ModelRiskLevel.High
        };
        var statement = fused.State switch
        {
            EvidenceFusionState.Convergent => $"{claimKey} = {fused.PreferredValue}",
            EvidenceFusionState.Insufficient => $"Insufficient independent evidence for {claimKey}.",
            EvidenceFusionState.Contested => $"Contested evidence for {claimKey}: " + string.Join(" | ", fused.Alternatives.Select(item => item.RepresentativeValue)),
            _ => throw new InvalidOperationException("Unknown fusion state.")
        };

        return Task.FromResult(new ModelAnalysisCandidate(
            ProviderId, "fusion-v1", statement, fused.Confidence, risk, fused.EvidenceIds,
            fused.State == EvidenceFusionState.Convergent ? Array.Empty<string>() : new[] { "Fusion is not convergent." },
            fused.State == EvidenceFusionState.Convergent ? Array.Empty<string>() : new[] { "Collect more independent evidence." }));
    }
}
