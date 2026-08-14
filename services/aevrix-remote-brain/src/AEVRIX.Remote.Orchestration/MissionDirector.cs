using System.Collections.Concurrent;

namespace Aevrix.Remote.Orchestration;

public enum MissionSpecialistKind
{
    StaticAnalysis,
    DynamicAnalysis,
    VisionOcr,
    NetworkBehavior,
    StructuralAnalysis,
    Documentation,
    Reconstruction,
    QuantumHybrid
}

public enum MissionTaskState
{
    Succeeded,
    Failed,
    Blocked
}

public sealed record MissionTaskSpec(
    string TaskId,
    MissionSpecialistKind Specialist,
    string Objective,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> DependsOn,
    bool Required = true)
{
    public MissionTaskSpec Validate()
    {
        if (!IsSafeId(TaskId, 3, 128))
        {
            throw new ArgumentException("Mission task id is invalid.", nameof(TaskId));
        }

        if (string.IsNullOrWhiteSpace(Objective) || Objective.Length > 16_000)
        {
            throw new ArgumentException("Mission task objective is invalid.", nameof(Objective));
        }

        if (EvidenceIds.Count > 2_000 || EvidenceIds.Any(id => !IsSafeId(id, 3, 160)))
        {
            throw new ArgumentException("Mission task evidence ids are invalid.", nameof(EvidenceIds));
        }

        if (DependsOn.Count > 256 || DependsOn.Any(id => !IsSafeId(id, 3, 128)))
        {
            throw new ArgumentException("Mission task dependencies are invalid.", nameof(DependsOn));
        }

        return this;
    }

    internal static bool IsSafeId(string value, int min, int max) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= min
        && value.Length <= max
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');
}

public sealed record MissionPlan(
    string MissionId,
    Guid ProjectId,
    string TargetId,
    IReadOnlyList<MissionTaskSpec> Tasks,
    int MaximumConcurrency = 4)
{
    public MissionPlan Validate()
    {
        if (!MissionTaskSpec.IsSafeId(MissionId, 3, 128))
        {
            throw new ArgumentException("Mission id is invalid.", nameof(MissionId));
        }

        if (ProjectId == Guid.Empty)
        {
            throw new ArgumentException("Mission project id cannot be empty.", nameof(ProjectId));
        }

        if (!MissionTaskSpec.IsSafeId(TargetId, 2, 128))
        {
            throw new ArgumentException("Mission target id is invalid.", nameof(TargetId));
        }

        if (Tasks.Count is < 1 or > 512)
        {
            throw new ArgumentException("Mission must contain between 1 and 512 tasks.", nameof(Tasks));
        }

        if (MaximumConcurrency is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrency));
        }

        foreach (var task in Tasks)
        {
            task.Validate();
        }

        var ids = Tasks.Select(task => task.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count != Tasks.Count)
        {
            throw new ArgumentException("Mission task ids must be unique.", nameof(Tasks));
        }

        foreach (var task in Tasks)
        {
            if (task.DependsOn.Any(dep => !ids.Contains(dep)))
            {
                throw new ArgumentException($"Mission task '{task.TaskId}' references an unknown dependency.", nameof(Tasks));
            }

            if (task.DependsOn.Any(dep => string.Equals(dep, task.TaskId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Mission task '{task.TaskId}' cannot depend on itself.", nameof(Tasks));
            }
        }

        EnsureAcyclic(Tasks);
        return this;
    }

    private static void EnsureAcyclic(IReadOnlyList<MissionTaskSpec> tasks)
    {
        var byId = tasks.ToDictionary(task => task.TaskId, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in tasks)
        {
            Visit(task.TaskId);
        }

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                throw new ArgumentException("Mission task dependency graph contains a cycle.", nameof(Tasks));
            }

            foreach (var dependency in byId[id].DependsOn)
            {
                Visit(dependency);
            }

            visiting.Remove(id);
            visited.Add(id);
        }
    }
}

public sealed record SpecialistExecutionContext(
    string MissionId,
    Guid ProjectId,
    string TargetId,
    MissionTaskSpec Task,
    IReadOnlyDictionary<string, SpecialistTaskResult> DependencyResults);

public sealed record SpecialistExecutionOutput(
    string Summary,
    double Confidence,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> ArtifactIds)
{
    public SpecialistExecutionOutput Validate()
    {
        if (string.IsNullOrWhiteSpace(Summary) || Summary.Length > 64_000)
        {
            throw new InvalidDataException("Specialist summary is invalid.");
        }

        if (!double.IsFinite(Confidence) || Confidence is < 0 or > 1)
        {
            throw new InvalidDataException("Specialist confidence is outside [0,1].");
        }

        if (EvidenceIds.Count > 2_000 || ArtifactIds.Count > 2_000)
        {
            throw new InvalidDataException("Specialist output exceeds evidence/artifact limits.");
        }

        return this;
    }
}

public sealed record SpecialistTaskResult(
    string TaskId,
    MissionSpecialistKind Specialist,
    MissionTaskState State,
    string Summary,
    double Confidence,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> ArtifactIds,
    string? ErrorType,
    DateTimeOffset CompletedAt);

public sealed record MissionExecutionResult(
    string MissionId,
    Guid ProjectId,
    string TargetId,
    IReadOnlyList<SpecialistTaskResult> TaskResults,
    bool RequiredTasksSucceeded,
    DateTimeOffset CompletedAt);

public interface IMissionSpecialist
{
    MissionSpecialistKind Kind { get; }
    Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed class MissionDirector
{
    private readonly IReadOnlyDictionary<MissionSpecialistKind, IMissionSpecialist> _specialists;
    private readonly TimeProvider _time;

    public MissionDirector(IEnumerable<IMissionSpecialist> specialists, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(specialists);
        _time = timeProvider ?? TimeProvider.System;

        var list = specialists.ToList();
        if (list.GroupBy(s => s.Kind).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Exactly one active specialist may be registered for each specialist kind.", nameof(specialists));
        }

        _specialists = list.ToDictionary(s => s.Kind);
    }

    public async Task<MissionExecutionResult> ExecuteAsync(
        MissionPlan plan,
        CancellationToken cancellationToken = default)
    {
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var missingKinds = plan.Tasks.Select(task => task.Specialist).Distinct().Where(kind => !_specialists.ContainsKey(kind)).ToArray();
        if (missingKinds.Length > 0)
        {
            throw new InvalidOperationException($"Mission cannot start because specialist(s) are unavailable: {string.Join(", ", missingKinds)}.");
        }

        var byId = plan.Tasks.ToDictionary(task => task.TaskId, StringComparer.OrdinalIgnoreCase);
        var results = new ConcurrentDictionary<string, SpecialistTaskResult>(StringComparer.OrdinalIgnoreCase);
        using var concurrency = new SemaphoreSlim(plan.MaximumConcurrency, plan.MaximumConcurrency);

        while (results.Count < plan.Tasks.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ready = plan.Tasks
                .Where(task => !results.ContainsKey(task.TaskId))
                .Where(task => task.DependsOn.All(results.ContainsKey))
                .ToArray();

            if (ready.Length == 0)
            {
                throw new InvalidOperationException("Mission scheduler made no progress despite a validated acyclic plan.");
            }

            var executions = ready.Select(task => ExecuteReadyTaskAsync(task)).ToArray();
            await Task.WhenAll(executions).ConfigureAwait(false);
        }

        var ordered = plan.Tasks.Select(task => results[task.TaskId]).ToArray();
        var requiredSucceeded = plan.Tasks
            .Where(task => task.Required)
            .All(task => results[task.TaskId].State == MissionTaskState.Succeeded);

        return new MissionExecutionResult(
            plan.MissionId,
            plan.ProjectId,
            plan.TargetId,
            ordered,
            requiredSucceeded,
            _time.GetUtcNow());

        async Task ExecuteReadyTaskAsync(MissionTaskSpec task)
        {
            var dependencyResults = task.DependsOn.ToDictionary(id => id, id => results[id], StringComparer.OrdinalIgnoreCase);
            if (dependencyResults.Values.Any(result => result.State != MissionTaskState.Succeeded))
            {
                results[task.TaskId] = new SpecialistTaskResult(
                    task.TaskId,
                    task.Specialist,
                    MissionTaskState.Blocked,
                    "Blocked because at least one dependency did not succeed.",
                    0,
                    [],
                    [],
                    "DependencyFailure",
                    _time.GetUtcNow());
                return;
            }

            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var context = new SpecialistExecutionContext(
                    plan.MissionId,
                    plan.ProjectId,
                    plan.TargetId,
                    task,
                    dependencyResults);

                try
                {
                    var output = (await _specialists[task.Specialist]
                        .ExecuteAsync(context, cancellationToken)
                        .ConfigureAwait(false)).Validate();

                    if (output.EvidenceIds.Except(task.EvidenceIds, StringComparer.OrdinalIgnoreCase).Any())
                    {
                        throw new InvalidDataException("Specialist output cites evidence outside the task evidence boundary.");
                    }

                    results[task.TaskId] = new SpecialistTaskResult(
                        task.TaskId,
                        task.Specialist,
                        MissionTaskState.Succeeded,
                        output.Summary,
                        output.Confidence,
                        output.EvidenceIds,
                        output.ArtifactIds,
                        null,
                        _time.GetUtcNow());
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    results[task.TaskId] = new SpecialistTaskResult(
                        task.TaskId,
                        task.Specialist,
                        MissionTaskState.Failed,
                        "Specialist execution failed.",
                        0,
                        [],
                        [],
                        ex.GetType().Name,
                        _time.GetUtcNow());
                }
            }
            finally
            {
                concurrency.Release();
            }
        }
    }
}
