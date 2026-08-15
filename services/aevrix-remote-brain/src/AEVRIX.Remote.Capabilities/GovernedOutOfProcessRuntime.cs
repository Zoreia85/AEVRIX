namespace Aevrix.Remote.Capabilities;

public sealed record OutOfProcessAuthorityPolicy(
    OutOfProcessNetworkPolicy Network,
    OutOfProcessFilesystemPolicy Filesystem)
{
    public OutOfProcessAuthorityPolicy Validate()
    {
        (Network ?? throw new ArgumentNullException(nameof(Network))).Validate();
        (Filesystem ?? throw new ArgumentNullException(nameof(Filesystem))).Validate();
        return this;
    }

    public bool RequiresUnavailableIsolationBackend =>
        Network.RequiresIsolation || Filesystem.RequiresIsolation;
}

public sealed record OutOfProcessAuthorityDecision(
    OutOfProcessNetworkScope NetworkScope,
    OutOfProcessFilesystemScope FilesystemScope,
    bool LaunchAuthorized,
    string DecisionCode)
{
    public bool RequiresNetworkIsolation => NetworkScope != OutOfProcessNetworkScope.Unrestricted;
    public bool RequiresFilesystemIsolation => FilesystemScope != OutOfProcessFilesystemScope.Unrestricted;
}

/// <summary>
/// Single execution-authority boundary for pinned local process adapters.
/// Callers that need governed execution should use this boundary instead of composing network
/// and filesystem gates independently. Until an OS-level enforcement backend is available,
/// any constrained network or filesystem authority fails closed before process launch.
/// </summary>
public sealed class GovernedOutOfProcessRuntime
{
    private readonly PinnedOutOfProcessRuntime _runtime;
    private readonly OutOfProcessAuthorityPolicy _authority;

    public GovernedOutOfProcessRuntime(
        PinnedOutOfProcessRuntime runtime,
        OutOfProcessAuthorityPolicy authority)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _authority = (authority ?? throw new ArgumentNullException(nameof(authority))).Validate();
    }

    public OutOfProcessAuthorityDecision EvaluateAuthority()
    {
        if (_authority.Network.RequiresIsolation && _authority.Filesystem.RequiresIsolation)
        {
            return new OutOfProcessAuthorityDecision(
                _authority.Network.Scope,
                _authority.Filesystem.Scope,
                false,
                "NetworkAndFilesystemIsolationBackendUnavailable");
        }

        if (_authority.Network.RequiresIsolation)
        {
            return new OutOfProcessAuthorityDecision(
                _authority.Network.Scope,
                _authority.Filesystem.Scope,
                false,
                "NetworkIsolationBackendUnavailable");
        }

        if (_authority.Filesystem.RequiresIsolation)
        {
            return new OutOfProcessAuthorityDecision(
                _authority.Network.Scope,
                _authority.Filesystem.Scope,
                false,
                "FilesystemIsolationBackendUnavailable");
        }

        return new OutOfProcessAuthorityDecision(
            _authority.Network.Scope,
            _authority.Filesystem.Scope,
            true,
            "AuthorizedUnrestrictedLocalProcess");
    }

    public Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var decision = EvaluateAuthority();
        if (!decision.LaunchAuthorized)
        {
            throw new InvalidOperationException(
                $"Pinned process launch denied by execution authority gate ({decision.DecisionCode}). " +
                $"Network={decision.NetworkScope}; Filesystem={decision.FilesystemScope}. " +
                "The current local-process runtime will not substitute broader host authority for a requested isolation boundary.");
        }

        return _runtime.ExecuteAsync(request, cancellationToken);
    }
}
