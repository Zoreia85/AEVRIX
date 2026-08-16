namespace Aevrix.Remote.Capabilities;

public enum OutOfProcessNetworkScope
{
    Unrestricted,
    None,
    LoopbackOnly,
    Allowlisted
}

public sealed record NetworkEndpointRule(string Host, int Port)
{
    public NetworkEndpointRule Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)
            || Host.Length > 253
            || Host.Any(char.IsWhiteSpace)
            || Host.Any(char.IsControl))
        {
            throw new ArgumentException("Network endpoint host is invalid.", nameof(Host));
        }

        if (Port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port));
        }

        return this;
    }
}

public sealed record OutOfProcessNetworkPolicy(
    OutOfProcessNetworkScope Scope,
    IReadOnlyList<NetworkEndpointRule>? AllowedEndpoints = null)
{
    public OutOfProcessNetworkPolicy Validate()
    {
        var endpoints = AllowedEndpoints ?? Array.Empty<NetworkEndpointRule>();
        if (endpoints.Count > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(AllowedEndpoints));
        }

        foreach (var endpoint in endpoints)
        {
            if (endpoint is null)
            {
                throw new ArgumentException("Network endpoint allowlist cannot contain null entries.", nameof(AllowedEndpoints));
            }

            endpoint.Validate();
        }

        if (Scope == OutOfProcessNetworkScope.Allowlisted && endpoints.Count == 0)
        {
            throw new ArgumentException("Allowlisted network scope requires at least one endpoint.", nameof(AllowedEndpoints));
        }

        if (Scope != OutOfProcessNetworkScope.Allowlisted && endpoints.Count != 0)
        {
            throw new ArgumentException("Network endpoints are only valid for Allowlisted scope.", nameof(AllowedEndpoints));
        }

        var duplicates = endpoints
            .GroupBy(endpoint => $"{endpoint.Host.Trim().ToLowerInvariant()}:{endpoint.Port}", StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
        if (duplicates)
        {
            throw new ArgumentException("Network endpoint allowlist contains duplicates.", nameof(AllowedEndpoints));
        }

        return this;
    }

    public bool RequiresIsolation => Scope != OutOfProcessNetworkScope.Unrestricted;
}

/// <summary>
/// Fail-closed network authority gate for the pinned process runtime.
/// The current local process runtime does not yet enforce host network isolation, so constrained
/// scopes are rejected before process launch instead of silently running with broader authority.
/// A future WFP/AppContainer/container backend can replace this gate once enforcement is proven.
/// </summary>
public sealed class NetworkGovernedOutOfProcessRuntime
{
    private readonly PinnedOutOfProcessRuntime _runtime;
    private readonly OutOfProcessNetworkPolicy _networkPolicy;

    public NetworkGovernedOutOfProcessRuntime(
        PinnedOutOfProcessRuntime runtime,
        OutOfProcessNetworkPolicy networkPolicy)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _networkPolicy = (networkPolicy ?? throw new ArgumentNullException(nameof(networkPolicy))).Validate();
    }

    public Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (_networkPolicy.RequiresIsolation)
        {
            throw new InvalidOperationException(
                $"Network scope '{_networkPolicy.Scope}' requires an enforcement backend. " +
                "The current pinned local-process runtime does not claim network isolation and will not launch with broader authority.");
        }

        return _runtime.ExecuteAsync(request, cancellationToken);
    }
}
