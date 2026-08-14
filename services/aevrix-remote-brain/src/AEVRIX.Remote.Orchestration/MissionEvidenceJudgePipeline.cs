namespace Aevrix.Remote.Orchestration;

public sealed record MissionKnowledgeRequest(MissionPlan Mission, IReadOnlyList<string> ClaimKeys, bool ValidateConvergentKnowledge = true)
{
    public MissionKnowledgeRequest Validate()
    {
        Mission.Validate();
        if (ClaimKeys is null || ClaimKeys.Count is < 1 or > 256 || ClaimKeys.Any(key => !MissionTaskSpec.IsSafeId(key, 3, 160)))
            throw new ArgumentException("Mission knowledge claim keys are invalid.", nameof(ClaimKeys));
        return this;
    }
}

public sealed record MissionKnowledgeItem(string ClaimKey, EvidenceFusionState FusionState, CandidateKnowledge Knowledge);
public sealed record MissionKnowledgeResult(MissionExecutionResult Mission, IReadOnlyList<MissionKnowledgeItem> KnowledgeItems);

public sealed class MissionEvidenceJudgePipeline
{
    private readonly MissionDirector _director;
    private readonly EvidenceBus _bus;
    private readonly EvidenceFusionEngine _fusion;
    private readonly ICandidateKnowledgeRepository _repository;
    private readonly IEvidenceValidationService _validator;
    private readonly TimeProvider _time;

    public MissionEvidenceJudgePipeline(MissionDirector director, EvidenceBus bus, EvidenceFusionEngine fusion,
        ICandidateKnowledgeRepository repository, IEvidenceValidationService validator, TimeProvider? timeProvider = null)
    {
        _director = director ?? throw new ArgumentNullException(nameof(director));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _fusion = fusion ?? throw new ArgumentNullException(nameof(fusion));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<MissionKnowledgeResult> ExecuteAsync(MissionKnowledgeRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var mission = await _director.ExecuteAsync(request.Mission, cancellationToken);
        if (!mission.RequiredTasksSucceeded)
            return new MissionKnowledgeResult(mission, Array.Empty<MissionKnowledgeItem>());

        var snapshot = _bus.Snapshot(mission.ProjectId, mission.TargetId);
        var judge = new OrchestratorJudge(new EvidenceFusionModelProvider(_bus, _fusion), _repository, _validator, timeProvider: _time);
        var items = new List<MissionKnowledgeItem>();

        foreach (var claimKey in request.ClaimKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observations = snapshot.Where(item => string.Equals(item.ClaimKey, claimKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (observations.Length == 0) continue;

            var fused = _fusion.Fuse(mission.ProjectId, mission.TargetId, claimKey, observations);
            var task = new AnalysisTask(BuildTaskId(mission.MissionId, claimKey), mission.ProjectId, mission.TargetId,
                $"Judge fused evidence claim '{claimKey}'.", fused.EvidenceIds,
                new Dictionary<string, string>
                {
                    [EvidenceFusionModelProvider.ClaimKeyContext] = claimKey,
                    ["fusion-state"] = fused.State.ToString()
                });

            var knowledge = await judge.AnalyzeToCandidateAsync(task, cancellationToken);
            if (request.ValidateConvergentKnowledge && fused.State == EvidenceFusionState.Convergent)
                knowledge = await judge.ValidateAndPromoteAsync(knowledge.KnowledgeId, cancellationToken);

            items.Add(new MissionKnowledgeItem(claimKey, fused.State, knowledge));
        }

        return new MissionKnowledgeResult(mission, items);
    }

    private static string BuildTaskId(string missionId, string claimKey)
    {
        var value = $"fusion:{missionId}:{claimKey}";
        if (value.Length <= 128) return value;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return "fusion:" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
