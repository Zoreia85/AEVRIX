using System.Collections.Concurrent;

namespace Aevrix.Remote.Capabilities;

public enum CapabilityApprovalState
{
    Unreviewed,
    Approved,
    Rejected
}

public enum AgentIsolationLevel
{
    Container,
    VirtualMachine,
    LocalProcess
}

public sealed record CapabilitySource(
    string RepositoryFullName,
    string SpdxLicense,
    string PinnedRevision,
    string ContentSha256)
{
    public CapabilitySource Validate()
    {
        if (!IsSafeRepositoryName(RepositoryFullName))
        {
            throw new ArgumentException("Capability source repository must be an owner/name GitHub identifier.", nameof(RepositoryFullName));
        }

        if (string.IsNullOrWhiteSpace(SpdxLicense) || SpdxLicense.Length > 80)
        {
            throw new ArgumentException("Capability source SPDX license is missing or invalid.", nameof(SpdxLicense));
        }

        if (!(PinnedRevision.Length is 40 or 64) || !IsNonZeroHex(PinnedRevision))
        {
            throw new ArgumentException("Capability source revision must be a full non-zero Git object id (40 or 64 hexadecimal characters).", nameof(PinnedRevision));
        }

        if (ContentSha256.Length != 64 || !IsNonZeroHex(ContentSha256))
        {
            throw new ArgumentException("Capability source content hash must be a non-zero SHA-256 hexadecimal digest.", nameof(ContentSha256));
        }

        return this;
    }

    private static bool IsSafeRepositoryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            return false;
        }

        var pieces = value.Split('/', StringSplitOptions.None);
        return pieces.Length == 2
            && pieces.All(piece => piece.Length is > 0 and <= 100
                && piece.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'));
    }

    private static bool IsNonZeroHex(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(Uri.IsHexDigit)
        && value.Any(ch => ch != '0');
}

public static class AevrixCapabilityPolicy
{
    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "anti-bot-bypass",
        "captcha-bypass",
        "cloudflare-bypass",
        "access-control-evasion",
        "unrestricted-host-filesystem",
        "automatic-server-install",
        "automatic-server-execution",
        "automatic-credential-use",
        "implicit-plugin-execution",
        "unreviewed-plugin-execution"
    };

    public static bool IsDenied(string capability) =>
        !string.IsNullOrWhiteSpace(capability) && Denied.Contains(capability.Trim());

    public static void EnsureAllowed(IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var denied = capabilities
            .Where(IsDenied)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (denied.Length > 0)
        {
            throw new InvalidOperationException(
                $"Capability set contains denied runtime behavior: {string.Join(", ", denied)}.");
        }
    }
}

public sealed record McpServerDescriptor(
    string ServerId,
    Uri Endpoint,
    CapabilitySource Source,
    CapabilityApprovalState Approval,
    bool ReadOnly,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> RequiredSecretNames,
    IReadOnlyList<string> AllowedFilesystemRoots)
{
    public McpServerDescriptor Validate()
    {
        ValidateId(ServerId, nameof(ServerId));
        ValidateEndpoint(Endpoint, nameof(Endpoint));
        Source.Validate();
        ArgumentNullException.ThrowIfNull(Capabilities);
        ArgumentNullException.ThrowIfNull(RequiredSecretNames);
        ArgumentNullException.ThrowIfNull(AllowedFilesystemRoots);

        if (Capabilities.Count > 256 || RequiredSecretNames.Count > 64 || AllowedFilesystemRoots.Count > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(Capabilities), "MCP descriptor exceeds bounded capability/resource limits.");
        }

        if (RequiredSecretNames.Any(secret => !IsSafeToken(secret, 1, 120)))
        {
            throw new ArgumentException("MCP secret names must be identifiers only; secret values are never stored in registry metadata.", nameof(RequiredSecretNames));
        }

        if (AllowedFilesystemRoots.Any(root => string.IsNullOrWhiteSpace(root) || root.Length > 1_024))
        {
            throw new ArgumentException("MCP filesystem roots are invalid.", nameof(AllowedFilesystemRoots));
        }

        return this;
    }

    public bool CanConnect()
    {
        try
        {
            Validate();
            AevrixCapabilityPolicy.EnsureAllowed(Capabilities);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }

        return Approval == CapabilityApprovalState.Approved;
    }

    internal static void ValidateEndpoint(Uri endpoint, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.IsNullOrEmpty(endpoint.Query))
        {
            throw new ArgumentException("Capability endpoint must be an absolute URI without credentials, query, or fragment.", parameterName);
        }

        var schemeAllowed = string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && endpoint.IsLoopback);

        if (!schemeAllowed)
        {
            throw new ArgumentException("Capability endpoint must use HTTPS, except loopback HTTP endpoints.", parameterName);
        }
    }

    internal static void ValidateId(string value, string parameterName)
    {
        if (!IsSafeToken(value, 2, 120))
        {
            throw new ArgumentException("Capability identifier is invalid.", parameterName);
        }
    }

    private static bool IsSafeToken(string value, int minimumLength, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= minimumLength
        && value.Length <= maximumLength
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');
}

public sealed record AgentBackendDescriptor(
    string BackendId,
    Uri Endpoint,
    CapabilitySource Source,
    AgentIsolationLevel Isolation,
    CapabilityApprovalState Approval,
    IReadOnlyList<string> AllowedProjectRoots,
    bool HostFilesystemMounted,
    bool OutboundNetworkAllowed)
{
    public AgentBackendDescriptor Validate()
    {
        McpServerDescriptor.ValidateId(BackendId, nameof(BackendId));
        McpServerDescriptor.ValidateEndpoint(Endpoint, nameof(Endpoint));
        Source.Validate();
        ArgumentNullException.ThrowIfNull(AllowedProjectRoots);

        if (AllowedProjectRoots.Count is 0 or > 64
            || AllowedProjectRoots.Any(root => string.IsNullOrWhiteSpace(root) || root.Length > 1_024))
        {
            throw new ArgumentException("Agent backend requires one or more bounded project roots.", nameof(AllowedProjectRoots));
        }

        return this;
    }

    public bool CanRun()
    {
        try
        {
            Validate();
        }
        catch (ArgumentException)
        {
            return false;
        }

        return Approval == CapabilityApprovalState.Approved
            && Isolation is AgentIsolationLevel.Container or AgentIsolationLevel.VirtualMachine
            && !HostFilesystemMounted;
    }
}

public sealed class CapabilityRegistry
{
    private readonly ConcurrentDictionary<string, McpServerDescriptor> _mcpServers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentBackendDescriptor> _agentBackends =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterMcp(McpServerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Validate();

        if (descriptor.Approval == CapabilityApprovalState.Approved)
        {
            AevrixCapabilityPolicy.EnsureAllowed(descriptor.Capabilities);
        }

        _mcpServers[descriptor.ServerId] = descriptor;
    }

    public void RegisterAgentBackend(AgentBackendDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Validate();
        _agentBackends[descriptor.BackendId] = descriptor;
    }

    public IReadOnlyList<McpServerDescriptor> ConnectableMcpServers() =>
        _mcpServers.Values
            .Where(server => server.CanConnect())
            .OrderBy(server => server.ServerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<AgentBackendDescriptor> RunnableAgentBackends() =>
        _agentBackends.Values
            .Where(backend => backend.CanRun())
            .OrderBy(backend => backend.BackendId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
