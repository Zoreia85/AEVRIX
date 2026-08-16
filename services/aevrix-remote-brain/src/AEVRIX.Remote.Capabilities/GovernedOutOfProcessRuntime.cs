using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// Deterministic, non-secret binding for the exact authority profile. Backends must return
    /// this value after execution so an attestation cannot be accidentally replayed across a
    /// different network/filesystem policy.
    /// </summary>
    public string ComputeFingerprint()
    {
        Validate();
        var endpoints = (Network.AllowedEndpoints ?? Array.Empty<NetworkEndpointRule>())
            .Select(endpoint => $"{endpoint.Host.Trim().ToLowerInvariant()}:{endpoint.Port}")
            .OrderBy(value => value, StringComparer.Ordinal);
        var canonical = string.Join("\n", new[]
        {
            "aevrix-authority-v1",
            $"network={Network.Scope}",
            $"endpoints={string.Join("|", endpoints)}",
            $"filesystem={Filesystem.Scope}"
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
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

public sealed record IsolationAuthorityAttestation(
    string BackendId,
    string AuthorityFingerprint,
    bool FilesystemWriteBoundaryEnforced = false,
    bool FilesystemReadIsolationEnforced = false)
{
    public IsolationAuthorityAttestation Validate()
    {
        if (!GovernedOutOfProcessRuntime.IsSafeBackendId(BackendId))
        {
            throw new InvalidDataException("Isolation attestation backend id is invalid.");
        }
        if (string.IsNullOrWhiteSpace(AuthorityFingerprint)
            || AuthorityFingerprint.Length != 64
            || !AuthorityFingerprint.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Isolation attestation authority fingerprint is invalid.");
        }
        return this;
    }
}

/// <summary>
/// Replaceable execution backend for one authority profile. Backends may be local-process,
/// AppContainer/restricted-token, container or VM implementations. Claiming support is not
/// sufficient: the returned execution attestation is rechecked by the authority boundary.
/// Filesystem-restricted authority requires independent proof of both an external-write boundary
/// and external-read isolation; one must never be inferred from the other.
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

    IsolationAuthorityAttestation AttestAuthority(
        OutOfProcessAuthorityPolicy authority,
        OutOfProcessExecutionResult execution);
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

    public IsolationAuthorityAttestation AttestAuthority(
        OutOfProcessAuthorityPolicy authority,
        OutOfProcessExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(execution);
        return new IsolationAuthorityAttestation(BackendId, authority.ComputeFingerprint());
    }
}

/// <summary>
/// Single execution-authority boundary for pinned adapters. It chooses only a registered backend
/// that declares it can enforce the complete network/filesystem authority profile. After execution,
/// the boundary independently verifies enforcement flags, granular filesystem proof when required,
/// and an attestation binding the exact backend identity to the exact requested authority fingerprint.
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
        ValidateAttestation(backend, result);
        return result;
    }

    private void ValidateAttestation(IOutOfProcessIsolationBackend backend, OutOfProcessExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(result);
        var attestation = result.Attestation ?? throw new InvalidDataException("Execution backend returned no execution attestation.");
        var binding = (backend.AttestAuthority(_authority, result)
            ?? throw new InvalidDataException("Execution backend returned no authority binding.")).Validate();

        if (!string.Equals(binding.BackendId, backend.BackendId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Execution authority attestation is bound to a different backend identity.");
        }

        var expected = _authority.ComputeFingerprint();
        var expectedBytes = Convert.FromHexString(expected);
        var actualBytes = Convert.FromHexString(binding.AuthorityFingerprint);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            {
                throw new InvalidDataException("Execution authority attestation is bound to a different authority policy.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }

        if (_authority.Network.RequiresIsolation && !attestation.NetworkIsolationEnforced)
        {
            throw new InvalidDataException("Selected execution backend did not attest the required network isolation.");
        }

        if (_authority.Filesystem.RequiresIsolation)
        {
            if (!attestation.FilesystemIsolationEnforced)
            {
                throw new InvalidDataException("Selected execution backend did not attest the required filesystem isolation.");
            }
            if (!binding.FilesystemWriteBoundaryEnforced)
            {
                throw new InvalidDataException("Selected execution backend did not attest an external-write filesystem boundary.");
            }
            if (!binding.FilesystemReadIsolationEnforced)
            {
                throw new InvalidDataException("Selected execution backend did not attest external-read filesystem isolation.");
            }
        }
    }

    internal static bool IsSafeBackendId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= 3 and <= 96
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
}
