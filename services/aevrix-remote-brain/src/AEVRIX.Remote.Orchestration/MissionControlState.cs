using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public enum MissionControlPhase
{
    Planned,
    Authorized,
    Executing,
    Evaluating,
    Judged,
    Admitted,
    Blocked,
    BlueprintEligible,
    NotEligible
}

public sealed record MissionControlSnapshot(
    string MissionId,
    Guid ProjectId,
    string WorkspaceId,
    string SubjectId,
    string TargetId,
    MissionControlPhase Phase,
    long Sequence,
    DateTimeOffset ObservedAtUtc,
    string PreviousStateDigestSha256,
    string StateDigestSha256,
    string Reason)
{
    public static MissionControlSnapshot CreateInitial(
        GovernedMissionPlan plan,
        DateTimeOffset observedAtUtc,
        string reason = "Mission plan admitted to the control plane.")
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        return Create(
            plan.ExecutionPlan.MissionId,
            plan.Scope.ProjectId,
            plan.Scope.WorkspaceId,
            plan.Scope.SubjectId,
            plan.Scope.TargetId,
            MissionControlPhase.Planned,
            0,
            observedAtUtc,
            new string('0', 64),
            reason);
    }

    internal static MissionControlSnapshot Create(
        string missionId,
        Guid projectId,
        string workspaceId,
        string subjectId,
        string targetId,
        MissionControlPhase phase,
        long sequence,
        DateTimeOffset observedAtUtc,
        string previousDigest,
        string reason)
    {
        ValidateReason(reason);
        var digest = ComputeDigest(
            missionId, projectId, workspaceId, subjectId, targetId,
            phase, sequence, observedAtUtc, previousDigest, reason);

        return new MissionControlSnapshot(
            missionId, projectId, workspaceId, subjectId, targetId,
            phase, sequence, observedAtUtc, previousDigest, digest, reason);
    }

    public void Verify()
    {
        if (!MissionTaskSpec.IsSafeId(MissionId, 3, 128)
            || ProjectId == Guid.Empty
            || !MissionTaskSpec.IsSafeId(WorkspaceId, 3, 128)
            || !MissionTaskSpec.IsSafeId(SubjectId, 3, 128)
            || !MissionTaskSpec.IsSafeId(TargetId, 2, 128)
            || Sequence < 0)
            throw new InvalidDataException("Mission control snapshot identity is invalid.");

        ValidateDigest(PreviousStateDigestSha256, nameof(PreviousStateDigestSha256));
        ValidateDigest(StateDigestSha256, nameof(StateDigestSha256));
        ValidateReason(Reason);

        var expected = ComputeDigest(
            MissionId, ProjectId, WorkspaceId, SubjectId, TargetId,
            Phase, Sequence, ObservedAtUtc, PreviousStateDigestSha256, Reason);

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(StateDigestSha256)))
            throw new InvalidDataException("Mission control snapshot digest is invalid.");
    }

    private static string ComputeDigest(
        string missionId,
        Guid projectId,
        string workspaceId,
        string subjectId,
        string targetId,
        MissionControlPhase phase,
        long sequence,
        DateTimeOffset observedAtUtc,
        string previousDigest,
        string reason)
    {
        var canonical = string.Join('\n',
            "AEVRIX-MISSION-CONTROL-V1",
            missionId,
            projectId.ToString("D"),
            workspaceId,
            subjectId,
            targetId,
            phase.ToString(),
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            observedAtUtc.ToUniversalTime().ToString("O"),
            previousDigest.ToUpperInvariant(),
            reason);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateDigest(string value, string name)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException($"{name} is not a SHA-256 digest.");
    }

    private static void ValidateReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048)
            throw new ArgumentException("Mission control reason is invalid.", nameof(value));
    }
}

public static class MissionControlStateMachine
{
    public static MissionControlSnapshot Advance(
        MissionControlSnapshot current,
        MissionControlPhase next,
        DateTimeOffset observedAtUtc,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Verify();

        if (!CanTransition(current.Phase, next))
            throw new InvalidOperationException($"Mission control transition {current.Phase} -> {next} is not allowed.");
        if (observedAtUtc < current.ObservedAtUtc)
            throw new InvalidOperationException("Mission control time cannot move backwards.");

        return MissionControlSnapshot.Create(
            current.MissionId,
            current.ProjectId,
            current.WorkspaceId,
            current.SubjectId,
            current.TargetId,
            next,
            checked(current.Sequence + 1),
            observedAtUtc,
            current.StateDigestSha256,
            reason);
    }

    public static bool CanTransition(MissionControlPhase current, MissionControlPhase next) =>
        current switch
        {
            MissionControlPhase.Planned => next is MissionControlPhase.Authorized or MissionControlPhase.Blocked,
            MissionControlPhase.Authorized => next is MissionControlPhase.Executing or MissionControlPhase.Blocked,
            MissionControlPhase.Executing => next is MissionControlPhase.Evaluating or MissionControlPhase.Blocked,
            MissionControlPhase.Evaluating => next is MissionControlPhase.Judged or MissionControlPhase.Blocked,
            MissionControlPhase.Judged => next is MissionControlPhase.Admitted or MissionControlPhase.Blocked,
            MissionControlPhase.Admitted => next is MissionControlPhase.BlueprintEligible or MissionControlPhase.NotEligible,
            _ => false
        };
}
