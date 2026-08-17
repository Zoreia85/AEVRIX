namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Security-relevant version actually offered for one governed execution. Epochs are monotonic
/// integers rather than display/package versions so ordering is unambiguous and downgrade checks
/// do not depend on semantic-version parsing.
/// </summary>
public sealed record ExecutionVersionStamp(
    ulong BackendSecurityEpoch,
    ulong AuthorityPolicyEpoch)
{
    public ExecutionVersionStamp Validate()
    {
        if (BackendSecurityEpoch == 0)
            throw new InvalidDataException("Backend security epoch must be explicitly versioned.");
        if (AuthorityPolicyEpoch == 0)
            throw new InvalidDataException("Authority policy epoch must be explicitly versioned.");
        return this;
    }
}

/// <summary>
/// Minimum security epochs that may execute. A production anchor must persist this value outside
/// the rollback domain of the runtime binary, local policy file and project checkpoint.
/// </summary>
public sealed record ExecutionVersionFloor(
    ulong MinimumBackendSecurityEpoch,
    ulong MinimumAuthorityPolicyEpoch)
{
    internal static ExecutionVersionFloor Empty { get; } = new(0, 0);

    public ExecutionVersionFloor Validate()
    {
        if (MinimumBackendSecurityEpoch == 0)
            throw new InvalidDataException("Backend security floor must be explicitly provisioned.");
        if (MinimumAuthorityPolicyEpoch == 0)
            throw new InvalidDataException("Authority policy floor must be explicitly provisioned.");
        return this;
    }

    public bool Allows(ExecutionVersionStamp candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.Validate();
        Validate();
        return candidate.BackendSecurityEpoch >= MinimumBackendSecurityEpoch
            && candidate.AuthorityPolicyEpoch >= MinimumAuthorityPolicyEpoch;
    }

    internal bool IsMonotonicFrom(ExecutionVersionFloor previous) =>
        MinimumBackendSecurityEpoch >= previous.MinimumBackendSecurityEpoch
        && MinimumAuthorityPolicyEpoch >= previous.MinimumAuthorityPolicyEpoch;
}

/// <summary>
/// Independent compare-and-swap authority for the minimum executable/backend and sandbox-policy
/// security epochs. Implementations must live outside the rollback domain they protect.
/// </summary>
public interface IExecutionVersionFloorAnchor
{
    Task<ExecutionVersionFloor?> LoadAsync(
        string scopeId,
        CancellationToken cancellationToken = default);

    Task AdvanceAsync(
        string scopeId,
        ExecutionVersionFloor expectedPrevious,
        ExecutionVersionFloor next,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fail-closed monotonic downgrade gate. It never treats a missing floor as first-use permission:
/// provisioning is an explicit state transition, and execution remains denied until the external
/// anchor proves a non-zero floor.
/// </summary>
public sealed class MonotonicExecutionVersionGate
{
    private readonly IExecutionVersionFloorAnchor _anchor;
    private readonly string _scopeId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MonotonicExecutionVersionGate(IExecutionVersionFloorAnchor anchor, string scopeId)
    {
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        _scopeId = ValidateScope(scopeId);
    }

    public async Task<ExecutionVersionFloor> EnsureAllowedAsync(
        ExecutionVersionStamp candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var floor = await _anchor.LoadAsync(_scopeId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Execution version floor is not provisioned in the external monotonic authority.");
            floor.Validate();
            if (!floor.Allows(candidate))
            {
                throw new InvalidDataException(
                    $"Execution downgrade rejected. Candidate backend/policy epochs " +
                    $"{candidate.BackendSecurityEpoch}/{candidate.AuthorityPolicyEpoch} are below floor " +
                    $"{floor.MinimumBackendSecurityEpoch}/{floor.MinimumAuthorityPolicyEpoch}.");
            }
            return floor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExecutionVersionFloor> AdvanceFloorAsync(
        ExecutionVersionFloor next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        next.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _anchor.LoadAsync(_scopeId, cancellationToken).ConfigureAwait(false);
            var effectiveCurrent = current ?? ExecutionVersionFloor.Empty;

            if (current is not null)
            {
                current.Validate();
                if (current == next)
                    return current;
            }

            if (!next.IsMonotonicFrom(effectiveCurrent))
            {
                throw new InvalidDataException(
                    "Execution version floor cannot move backwards in either security epoch.");
            }

            await _anchor.AdvanceAsync(
                _scopeId,
                effectiveCurrent,
                next,
                cancellationToken).ConfigureAwait(false);

            var committed = await _anchor.LoadAsync(_scopeId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Execution version floor anchor lost the committed floor.");
            committed.Validate();
            if (committed != next)
            {
                throw new InvalidDataException(
                    "Execution version floor anchor did not commit the exact requested monotonic floor.");
            }
            return committed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ValidateScope(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length is < 3 or > 128
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':')))
        {
            throw new ArgumentException("Execution version floor scope id is invalid.", nameof(value));
        }
        return value;
    }
}
