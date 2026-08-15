namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Windows isolation backend for the exact authority profile Network=None + Filesystem=Unrestricted.
/// The wrapped pinned runtime must be configured with RequireAppContainer=true so the child token is
/// independently verified as AppContainer before ResumeThread. AEVRIX-created AppContainer profiles
/// carry zero capabilities; Windows therefore provides no network capability to the process.
/// Network=None additionally fails closed whenever the Windows global AppContainer loopback-exemption
/// table is non-empty, because an exemption could undermine the no-loopback assumption and this backend
/// deliberately does not guess which external exemption is harmless. This backend does not claim
/// filesystem isolation and does not support LoopbackOnly or endpoint allowlists.
/// </summary>
public sealed class WindowsZeroCapabilityAppContainerBackend : IOutOfProcessIsolationBackend
{
    private readonly PinnedOutOfProcessRuntime _runtime;
    private readonly IAppContainerLoopbackPolicyInspector _loopbackPolicy;

    public WindowsZeroCapabilityAppContainerBackend(
        PinnedOutOfProcessRuntime runtime,
        int priority = 100,
        IAppContainerLoopbackPolicyInspector? loopbackPolicy = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        if (priority is < -1_000 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }
        Priority = priority;
        _loopbackPolicy = loopbackPolicy ?? new WindowsAppContainerLoopbackPolicyInspector();
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

        EnsureNoLoopbackExemptions("before launch");
        var result = await _runtime.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureNoLoopbackExemptions("after execution");

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

    private void EnsureNoLoopbackExemptions(string phase)
    {
        var exemptionCount = _loopbackPolicy.GetLoopbackExemptionCount();
        if (exemptionCount < 0)
        {
            throw new InvalidDataException("AppContainer loopback policy inspector returned an invalid exemption count.");
        }
        if (exemptionCount != 0)
        {
            throw new InvalidOperationException(
                $"Network=None AppContainer execution is blocked {phase} because Windows reports {exemptionCount} global loopback exemption(s). AEVRIX will not guess that an external exemption is unrelated.");
        }
    }
}
