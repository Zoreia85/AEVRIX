namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Applies non-authoritative QIR routing hints to mission task ordering only.
/// The prioritizer cannot add/remove tasks, alter evidence, dependencies, required flags,
/// specialist identity, objectives, project/target scope or concurrency limits.
/// </summary>
public sealed class QirMissionPlanPrioritizer
{
    public MissionPlan Prioritize(MissionPlan plan, IReadOnlyCollection<QirMissionHint> hints)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(hints);
        plan.Validate();

        if (hints.Count > 32)
        {
            throw new InvalidDataException("QIR mission hint input exceeds safe bounds.");
        }

        foreach (var hint in hints)
        {
            if (hint is null)
            {
                throw new InvalidDataException("QIR mission hint cannot be null.");
            }
            if (!double.IsFinite(hint.PriorityScore) || hint.PriorityScore is < 0 or > 1)
            {
                throw new InvalidDataException("QIR mission hint priority is invalid.");
            }
            if (hint.IsEvidence || hint.CanSatisfyEvidenceRequirement || hint.CanDriveBlueprint)
            {
                throw new InvalidDataException("QIR mission hint attempted to cross its non-evidence trust boundary.");
            }
        }

        var priorityBySpecialist = hints
            .GroupBy(hint => hint.Specialist)
            .ToDictionary(group => group.Key, group => group.Max(hint => hint.PriorityScore));

        var originalIndex = plan.Tasks
            .Select((task, index) => (task.TaskId, index))
            .ToDictionary(item => item.TaskId, item => item.index, StringComparer.OrdinalIgnoreCase);

        var ordered = plan.Tasks
            .OrderBy(task => DependencyDepth(task, plan.Tasks))
            .ThenByDescending(task => priorityBySpecialist.GetValueOrDefault(task.Specialist, 0))
            .ThenBy(task => originalIndex[task.TaskId])
            .ToArray();

        var prioritized = plan with { Tasks = ordered };
        prioritized.Validate();
        EnsureAuthorityPreserved(plan, prioritized);
        return prioritized;
    }

    private static int DependencyDepth(MissionTaskSpec task, IReadOnlyList<MissionTaskSpec> tasks)
    {
        var byId = tasks.ToDictionary(item => item.TaskId, StringComparer.OrdinalIgnoreCase);
        var memo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return Depth(task.TaskId);

        int Depth(string id)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            var item = byId[id];
            var value = item.DependsOn.Count == 0 ? 0 : item.DependsOn.Max(dep => Depth(dep)) + 1;
            memo[id] = value;
            return value;
        }
    }

    private static void EnsureAuthorityPreserved(MissionPlan before, MissionPlan after)
    {
        if (before.ProjectId != after.ProjectId
            || !string.Equals(before.MissionId, after.MissionId, StringComparison.Ordinal)
            || !string.Equals(before.TargetId, after.TargetId, StringComparison.Ordinal)
            || before.MaximumConcurrency != after.MaximumConcurrency
            || before.Tasks.Count != after.Tasks.Count)
        {
            throw new InvalidOperationException("QIR prioritization changed mission authority or scope.");
        }

        var afterById = after.Tasks.ToDictionary(task => task.TaskId, StringComparer.OrdinalIgnoreCase);
        foreach (var original in before.Tasks)
        {
            if (!afterById.TryGetValue(original.TaskId, out var candidate) || candidate != original)
            {
                throw new InvalidOperationException("QIR prioritization changed a governed mission task.");
            }
        }
    }
}
