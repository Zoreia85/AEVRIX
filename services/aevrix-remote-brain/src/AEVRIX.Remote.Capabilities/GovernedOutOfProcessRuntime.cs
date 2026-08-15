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
    string DecisionCode,
    string? SelectedBackendId = null)
{
    public bool RequiresNetworkIsolation => NetworkScope != OutOfProcessNetworkScope.Unrestricted;
    public bool RequiresFilesystemIsolation => FilesystemScope != OutOfProcessFilesystemScope.Unrestricted;
}

/// <summary>
/// Replaceable execution backend for one authority profile. Backends may be local-process,
/// AppContainer/restricted-token, container or VM implementations. Claiming support is not
/// sufficient: the returned execution attestation is rechecked by the authority boundary.
/// </summary>
public interface IOutOfProcessIsolationBackend
{
    string BackendId { get; }
    int Priority { get; }
    bool CanEnforce(OutOfProcessAuthorityPolicy authority);
    Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessAuthorityPolicy authority,
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Current local-process backend. It deliberately supports unrestricted host authority only.
/// Restricted network/filesystem policies remain unavailable until an OS-level backend proves
/// the corresponding enforcement in its execution attestation.
/// </summary>
public sealed class LocalUnrestrictedOutOfProcessBackend : IOutOfProcessIsolationBackend
{
    private readonly PinnedOutOfProcessRuntime _runtime;

    public LocalUnrestrictedOutOfProcessBackend(PinnedOutOfProcessRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public string BackendId => "local-unrestricted";
    public int Priority => 0;

    public bool CanEnforce(OutOfProcessAuthorityPolicy authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.Validate();
        return !authority.RequiresUnavailableIsolationBackend;
    }

    public Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessAuthorityPolicy authority,
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(request);
        authority.Validate();
        request.Validate();
        if (!CanEnforce(authority))
        {
            throw new InvalidOperationException("The unrestricted local-process backend cannot enforce the requested isolation authority.");
        }

        return _runtime.ExecuteAsync(request, cancellationToken);
    }
}

/// <summary>
/// Single execution-authority boundary for pinned adapters. It chooses only a registered backend
/// that declares it can enforce the complete network/filesystem authority profile. After execution,
/// the boundary independently verifies the backend attestation before returning the result.
/// </summary>
public sealed class GovernedOutOfProcessRuntime
{
    private readonly IReadOnlyList<IOutOfProcessIsolationBackend> _backends;
    private readonly OutOfProcessAuthorityPolicy _authority;

    public GovernedOutOfProcessRuntime(
        PinnedOutOfProcessRuntime runtime,
        OutOfProcessAuthorityPolicy authority)
        : this([new LocalUnrestrictedOutOfProcessBackend(runtime)], authority)
    {
    }

    public GovernedOutOfProcessRuntime(
        IEnumerable<IOutOfProcessIsolationBackend> backends,
        OutOfProcessAuthorityPolicy authority)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _authority = (authority ?? throw new ArgumentNullException(nameof(authority))).Validate();

        var list = backends.ToArray();
        if (list.Length is < 1 or > 16)
        {
            throw new ArgumentException("Between one and sixteen isolation backends must be registered.", nameof(backends));
        }

        foreach (var backend in list)
        {
            if (backend is null
                || !IsSafeBackendId(backend.BackendId)
                || backend.Priority is < -1_000 or > 1_000)
            {
                throw new ArgumentException("Isolation backend identity or priority is invalid.", nameof(backends));
            }
        }

        if (list.Select(item => item.BackendId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Length)
        {
            throw new ArgumentException("Isolation backend ids must be unique.", nameof(backends));
        }

        _backends = list
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.BackendId, StringComparer.Ordinal)
            .ToArray();
    }

    public OutOfProcessAuthorityDecision EvaluateAuthority()
    {
        var selected = _backends.FirstOrDefault(backend => backend.CanEnforce(_authority));
        if (selected is not null)
        {
            return new OutOfProcessAuthorityDecision(
                _authority.Network.Scope,
                _authority.Filesystem.Scope,
                true,
                selected is LocalUnrestrictedOutOfProcessBackend
                    ? "AuthorizedUnrestrictedLocalProcess"
                    : "AuthorizedByIsolationBackend",
                selected.BackendId);
        }

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
            false,
            "NoCompatibleExecutionBackend");
    }

    public async Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var decision = EvaluateAuthority();
        if (!decision.LaunchAuthorized || decision.SelectedBackendId is null)
        {
            throw new InvalidOperationException(
                $"Pinned process launch denied by execution authority gate ({decision.DecisionCode}). " +
                $"Network={decision.NetworkScope}; Filesystem={decision.FilesystemScope}. " +
                "No registered backend can prove the complete requested isolation boundary.");
        }

        var backend = _backends.Single(item =>
            string.Equals(item.BackendId, decision.SelectedBackendId, StringComparison.OrdinalIgnoreCase));
        var result = await backend.ExecuteAsync(_authority, request, cancellationToken).ConfigureAwait(false);
        ValidateAttestation(result.Attestation);
        return result;
    }

    private void ValidateAttestation(OutOfProcessExecutionAttestation attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        if (_authority.Network.RequiresIsolation && !attestation.NetworkIsolationEnforced)
        {
            throw new InvalidDataException("Selected execution backend did not attest the required network isolation.");
        }

        if (_authority.Filesystem.RequiresIsolation && !attestation.FilesystemIsolationEnforced)
        {
            throw new InvalidDataException("Selected execution backend did not attest the required filesystem isolation.");
        }
    }

    private static bool IsSafeBackendId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= 3 and <= 96
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
}
