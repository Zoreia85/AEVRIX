namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Explicit security-version registration for an isolation backend. Display/package versions are
/// intentionally not used for ordering: this epoch advances only when the backend's security
/// contract changes in a way that must never be silently rolled back.
/// </summary>
public sealed record VersionedIsolationBackendRegistration(
    IOutOfProcessIsolationBackend Backend,
    ulong BackendSecurityEpoch)
{
    public VersionedIsolationBackendRegistration Validate()
    {
        ArgumentNullException.ThrowIfNull(Backend);
        if (!GovernedOutOfProcessRuntime.IsSafeBackendId(Backend.BackendId))
            throw new ArgumentException("Versioned isolation backend id is invalid.", nameof(Backend));
        if (BackendSecurityEpoch == 0)
            throw new ArgumentOutOfRangeException(nameof(BackendSecurityEpoch), "Backend security epoch must be explicit and non-zero.");
        return this;
    }
}

/// <summary>
/// Governed runtime variant that refuses to execute any backend before an external monotonic floor
/// proves both the backend security epoch and the authority-policy epoch are current enough.
/// Every backend is wrapped internally; callers cannot accidentally mix guarded and unguarded
/// registrations inside this runtime. Activated-backend provenance remains available through the
/// same validated Gate 10 surface after the floor check succeeds.
/// </summary>
public sealed class DowngradeResistantGovernedOutOfProcessRuntime
{
    private readonly GovernedOutOfProcessRuntime _inner;

    public DowngradeResistantGovernedOutOfProcessRuntime(
        IEnumerable<VersionedIsolationBackendRegistration> registrations,
        OutOfProcessAuthorityPolicy authority,
        ulong authorityPolicyEpoch,
        MonotonicExecutionVersionGate versionGate)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(versionGate);
        if (authorityPolicyEpoch == 0)
            throw new ArgumentOutOfRangeException(nameof(authorityPolicyEpoch), "Authority policy epoch must be explicit and non-zero.");

        var list = registrations.Select(item =>
            (item ?? throw new ArgumentException("Versioned backend registration cannot be null.", nameof(registrations))).Validate()).ToArray();
        if (list.Length is < 1 or > 16)
            throw new ArgumentException("Between one and sixteen versioned isolation backends must be registered.", nameof(registrations));
        if (list.Select(item => item.Backend.BackendId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Length)
            throw new ArgumentException("Versioned isolation backend ids must be unique.", nameof(registrations));

        var guarded = list.Select(item => (IOutOfProcessIsolationBackend)new FloorGuardedBackend(
            item.Backend,
            new ExecutionVersionStamp(item.BackendSecurityEpoch, authorityPolicyEpoch).Validate(),
            versionGate)).ToArray();

        _inner = new GovernedOutOfProcessRuntime(guarded, authority);
    }

    /// <summary>
    /// Reports only policy/backend compatibility. It deliberately does not consult the external
    /// monotonic floor and therefore must never be used as launch authorization.
    /// </summary>
    public OutOfProcessAuthorityDecision EvaluatePolicyEligibility() => _inner.EvaluateAuthority();

    public Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.ExecuteAsync(request, cancellationToken);

    public Task<GovernedOutOfProcessExecutionResult> ExecuteWithProvenanceAsync(
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.ExecuteWithProvenanceAsync(request, cancellationToken);

    private sealed class FloorGuardedBackend : IOutOfProcessIsolationBackend
    {
        private readonly IOutOfProcessIsolationBackend _inner;
        private readonly ExecutionVersionStamp _version;
        private readonly MonotonicExecutionVersionGate _gate;

        public FloorGuardedBackend(
            IOutOfProcessIsolationBackend inner,
            ExecutionVersionStamp version,
            MonotonicExecutionVersionGate gate)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _version = (version ?? throw new ArgumentNullException(nameof(version))).Validate();
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        }

        public string BackendId => _inner.BackendId;
        public int Priority => _inner.Priority;

        public bool CanEnforce(OutOfProcessAuthorityPolicy authority) => _inner.CanEnforce(authority);

        public async Task<OutOfProcessExecutionResult> ExecuteAsync(
            OutOfProcessAuthorityPolicy authority,
            OutOfProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            // Security invariant: this check must finish before the wrapped backend receives the
            // execution request. Missing/stale floor therefore means zero adapter instructions.
            await _gate.EnsureAllowedAsync(_version, cancellationToken).ConfigureAwait(false);
            return await _inner.ExecuteAsync(authority, request, cancellationToken).ConfigureAwait(false);
        }

        public IsolationAuthorityAttestation AttestAuthority(
            OutOfProcessAuthorityPolicy authority,
            OutOfProcessExecutionResult execution) =>
            _inner.AttestAuthority(authority, execution);
    }
}
