namespace Aevrix.Remote.Orchestration;

[Flags]
public enum MissionRuntimeAuthority
{
    None = 0,
    ReadProjectWorkspace = 1 << 0,
    WriteDerivedArtifacts = 1 << 1,
    ApprovedNetwork = 1 << 2,
    DynamicInstrumentation = 1 << 3,
    PersistentState = 1 << 4
}

public enum MissionAuthorizationClass
{
    ThirdPartyCleanRoom,
    ExplicitlyAuthorizedSystem,
    OwnedSystem
}

public sealed record MissionScope(
    string WorkspaceId,
    string SubjectId,
    Guid ProjectId,
    string TargetId,
    MissionAuthorizationClass AuthorizationClass)
{
    public MissionScope Validate()
    {
        if (!MissionTaskSpec.IsSafeId(WorkspaceId, 3, 128))
            throw new ArgumentException("Workspace id is invalid.", nameof(WorkspaceId));
        if (!MissionTaskSpec.IsSafeId(SubjectId, 3, 128))
            throw new ArgumentException("Subject id is invalid.", nameof(SubjectId));
        if (ProjectId == Guid.Empty)
            throw new ArgumentException("Project id cannot be empty.", nameof(ProjectId));
        if (!MissionTaskSpec.IsSafeId(TargetId, 2, 128))
            throw new ArgumentException("Target id is invalid.", nameof(TargetId));
        return this;
    }
}

public sealed record MissionTaskAuthority(
    string TaskId,
    MissionRuntimeAuthority Authority,
    IReadOnlyList<string> AllowedNetworkHosts,
    long MaximumWorkingSetBytes,
    TimeSpan MaximumDuration)
{
    public MissionTaskAuthority Validate(MissionScope scope)
    {
        if (!MissionTaskSpec.IsSafeId(TaskId, 3, 128))
            throw new ArgumentException("Task authority id is invalid.", nameof(TaskId));
        if (MaximumWorkingSetBytes is < 16_777_216 or > 137_438_953_472)
            throw new ArgumentOutOfRangeException(nameof(MaximumWorkingSetBytes));
        if (MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(MaximumDuration));
        if (AllowedNetworkHosts.Count > 128)
            throw new ArgumentException("Too many network hosts.", nameof(AllowedNetworkHosts));
        if (AllowedNetworkHosts.Any(static host =>
                string.IsNullOrWhiteSpace(host)
                || host.Length > 253
                || Uri.CheckHostName(host) == UriHostNameType.Unknown))
            throw new ArgumentException("Network host allowlist is invalid.", nameof(AllowedNetworkHosts));
        if (AllowedNetworkHosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != AllowedNetworkHosts.Count)
            throw new ArgumentException("Network host allowlist contains duplicates.", nameof(AllowedNetworkHosts));

        var networkGranted = Authority.HasFlag(MissionRuntimeAuthority.ApprovedNetwork);
        if (networkGranted != (AllowedNetworkHosts.Count > 0))
            throw new ArgumentException("ApprovedNetwork authority and host allowlist must agree.", nameof(Authority));

        if (scope.AuthorizationClass == MissionAuthorizationClass.ThirdPartyCleanRoom
            && Authority.HasFlag(MissionRuntimeAuthority.DynamicInstrumentation))
            throw new InvalidOperationException("Dynamic instrumentation is not allowed for third-party clean-room missions.");

        return this;
    }
}

public sealed record MissionPlanningHintDisposition(
    string ProviderId,
    string HintId,
    bool Accepted,
    string Reason)
{
    public MissionPlanningHintDisposition Validate()
    {
        if (!MissionTaskSpec.IsSafeId(ProviderId, 2, 96))
            throw new ArgumentException("Planning hint provider id is invalid.", nameof(ProviderId));
        if (!MissionTaskSpec.IsSafeId(HintId, 2, 128))
            throw new ArgumentException("Planning hint id is invalid.", nameof(HintId));
        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 1024)
            throw new ArgumentException("Planning hint disposition reason is invalid.", nameof(Reason));
        return this;
    }
}

public sealed record GovernedMissionPlan(
    MissionPlan ExecutionPlan,
    MissionScope Scope,
    IReadOnlyList<MissionTaskAuthority> TaskAuthorities,
    IReadOnlyList<MissionPlanningHintDisposition> PlanningHints,
    int SchemaVersion = 1)
{
    public GovernedMissionPlan Validate()
    {
        ArgumentNullException.ThrowIfNull(ExecutionPlan);
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentNullException.ThrowIfNull(TaskAuthorities);
        ArgumentNullException.ThrowIfNull(PlanningHints);

        if (SchemaVersion != 1)
            throw new InvalidDataException("Unsupported governed mission plan schema.");

        ExecutionPlan.Validate();
        Scope.Validate();

        if (ExecutionPlan.ProjectId != Scope.ProjectId
            || !string.Equals(ExecutionPlan.TargetId, Scope.TargetId, StringComparison.Ordinal))
            throw new InvalidDataException("Execution plan identity does not match the mission scope.");

        var expected = ExecutionPlan.Tasks.Select(static task => task.TaskId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = TaskAuthorities.Select(static item => item.TaskId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (actual.Count != TaskAuthorities.Count || !expected.SetEquals(actual))
            throw new InvalidDataException("Task authority coverage must match the execution plan exactly.");

        foreach (var authority in TaskAuthorities)
            authority.Validate(Scope);
        foreach (var hint in PlanningHints)
            hint.Validate();

        return this;
    }
}

public sealed record MissionPlanningRequest(
    MissionPlan ProposedExecutionPlan,
    MissionScope Scope,
    IReadOnlyList<MissionTaskAuthority> TaskAuthorities,
    IReadOnlyList<MissionPlanningHintDisposition> PlanningHints);

public interface IMissionPlanner
{
    Task<GovernedMissionPlan> CreateAsync(
        MissionPlanningRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic baseline planner. QIR or other optimizers may propose bounded hints,
/// but correctness never depends on an optimizer being present.
/// </summary>
public sealed class DeterministicMissionPlanner : IMissionPlanner
{
    public Task<GovernedMissionPlan> CreateAsync(
        MissionPlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var governed = new GovernedMissionPlan(
            request.ProposedExecutionPlan,
            request.Scope,
            request.TaskAuthorities,
            request.PlanningHints).Validate();

        return Task.FromResult(governed);
    }
}
