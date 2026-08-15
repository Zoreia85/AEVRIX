namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Windows isolation backend for the exact authority profile Network=None + Filesystem=Unrestricted.
/// The wrapped pinned runtime must be configured with RequireAppContainer=true so the child token is
/// independently verified as AppContainer before ResumeThread. AEVRIX-created AppContainer profiles
/// carry zero capabilities; Windows therefore provides no network capability to the process. This
/// backend does not claim filesystem isolation and does not support LoopbackOnly or endpoint allowlists.
/// </summary>
public sealed class WindowsZeroCapabilityAppContainerBackend : IOutOfProcessIsolationBackend
{
    private readonly PinnedOutOfProcessRuntime _runtime;

    public WindowsZeroCapabilityAppContainerBackend(PinnedOutOfProcessRuntime runtime, int priority = 100)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        if (priority is < -1_000 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }
        Priority = priority;
    }

    public string BackendId => "windows-appcontainer-no-network";
    public int Priority { get; }

    public bool CanEnforce(OutOfProcessAuthorityPolicy authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.Validate();
        return OperatingSystem.IsWindows()
            && authority.Network.Scope == OutOfProcessNetworkScope.None
            && authority.Filesystem.Scope == OutOfProcessFilesystemScope.Unrestricted;
    }

    public async Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessAuthorityPolicy authority,
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(request);
        authority.Validate();
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (!CanEnforce(authority))
        {
            throw new InvalidOperationException(
                "The zero-capability AppContainer backend only supports Network=None with an unrestricted filesystem authority profile.");
        }

        var result = await _runtime.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Attestation.AppContainerEnforced)
        {
            throw new InvalidDataException(
                "Pinned runtime did not prove that the child process was launched inside the required AppContainer.");
        }

        return result with
        {
            Attestation = result.Attestation with
            {
                NetworkIsolationEnforced = true
            }
        };
    }

    public IsolationAuthorityAttestation AttestAuthority(
        OutOfProcessAuthorityPolicy authority,
        OutOfProcessExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(execution);
        authority.Validate();
        if (!execution.Attestation.AppContainerEnforced || !execution.Attestation.NetworkIsolationEnforced)
        {
            throw new InvalidDataException("AppContainer no-network backend cannot attest an execution that did not prove AppContainer network isolation.");
        }
        return new IsolationAuthorityAttestation(BackendId, authority.ComputeFingerprint());
    }
}
