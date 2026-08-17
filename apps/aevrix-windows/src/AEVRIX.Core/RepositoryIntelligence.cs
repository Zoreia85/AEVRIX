namespace Aevrix.Core;

public enum RepositoryIntegrationMode
{
    Reference,
    DiscoverySeed,
    Adapter,
    OptionalTool,
    Vendored,
    Blocked
}

public enum RepositorySecurityReviewState
{
    NeedsReview,
    Approved,
    Rejected
}

public enum RepositoryGovernanceAuthority
{
    BootstrapProjection,
    AuditedManifest
}

public sealed record RepositoryIntelligenceRecord(
    string Owner,
    string Name,
    Uri CanonicalUrl,
    string Purpose,
    RepositoryIntegrationMode IntegrationMode,
    string? SpdxLicense,
    string? PinnedRevision,
    string? ContentSha256,
    RepositorySecurityReviewState SecurityReview,
    bool RuntimeAllowlisted,
    DateTimeOffset LastVerifiedAt,
    IReadOnlyList<string> AllowedCapabilities,
    IReadOnlyList<string> DeniedCapabilities)
{
    public string FullName => $"{Owner}/{Name}";

    public RepositoryGovernanceAuthority GovernanceAuthority { get; init; } = RepositoryGovernanceAuthority.BootstrapProjection;

    public string? ManifestRuntimeApproval { get; init; }

    public string? ObservedRevision { get; init; }

    public IReadOnlyList<RepositoryIntegrationMode>? IntegrationModes { get; init; }

    public IReadOnlyList<RepositoryIntegrationMode> EffectiveIntegrationModes =>
        IntegrationModes is { Count: > 0 } ? IntegrationModes : [IntegrationMode];

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Purpose);
        ArgumentNullException.ThrowIfNull(CanonicalUrl);
        ArgumentNullException.ThrowIfNull(AllowedCapabilities);
        ArgumentNullException.ThrowIfNull(DeniedCapabilities);

        if (!string.Equals(CanonicalUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(CanonicalUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(CanonicalUrl.AbsolutePath.Trim('/'), FullName, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(CanonicalUrl.Query)
            || !string.IsNullOrEmpty(CanonicalUrl.Fragment))
        {
            throw new ArgumentException("Canonical repository URL must be an exact HTTPS github.com owner/name URL.", nameof(CanonicalUrl));
        }

        if (LastVerifiedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(LastVerifiedAt));
        }

        if (ObservedRevision is not null && !IsPinnedRevision(ObservedRevision))
        {
            throw new ArgumentException("Observed revision must be a full Git revision when supplied.", nameof(ObservedRevision));
        }

        if (IntegrationModes is { Count: > 0 }
            && (!IntegrationModes.Contains(IntegrationMode) || IntegrationModes.Distinct().Count() != IntegrationModes.Count))
        {
            throw new InvalidOperationException("Projected integration modes must be unique and include the primary mode.");
        }

        if (GovernanceAuthority == RepositoryGovernanceAuthority.AuditedManifest
            && string.IsNullOrWhiteSpace(ManifestRuntimeApproval))
        {
            throw new InvalidOperationException("Audited-manifest records require the manifest runtime-approval decision.");
        }

        if (RuntimeAllowlisted && !CanExecute())
        {
            throw new InvalidOperationException("Runtime allowlisting requires audited-manifest authority and every executable-repository gate to pass.");
        }
    }

    public bool CanExecute()
    {
        if (GovernanceAuthority != RepositoryGovernanceAuthority.AuditedManifest
            || !string.Equals(ManifestRuntimeApproval, "Approved", StringComparison.Ordinal))
        {
            return false;
        }

        var modes = EffectiveIntegrationModes;
        if (modes.Contains(RepositoryIntegrationMode.Blocked)
            || modes.Contains(RepositoryIntegrationMode.DiscoverySeed)
            || !modes.Any(mode => mode is RepositoryIntegrationMode.Adapter or RepositoryIntegrationMode.OptionalTool or RepositoryIntegrationMode.Vendored))
        {
            return false;
        }

        return RuntimeAllowlisted
            && SecurityReview == RepositorySecurityReviewState.Approved
            && HasVerifiedLicense(SpdxLicense)
            && IsPinnedRevision(PinnedRevision)
            && IsSha256(ContentSha256);
    }

    private static bool IsPinnedRevision(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
    }

    private static bool HasVerifiedLicense(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return !string.Equals(normalized, "NOASSERTION", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "NONE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "UNKNOWN", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }
}

public static class RepositoryIntelligenceCatalog
{
    public const string CanonicalManifestPath = "docs/manifests/repository-intelligence.json";

    private static readonly DateTimeOffset VerifiedAt = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MxcVerifiedAt = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<RepositoryIntelligenceRecord> InitialSeeds { get; } =
    [
        Seed("ollama", "ollama", "Local/open-model runtime candidate", RepositoryIntegrationMode.OptionalTool, "MIT", ["loopback-model-inference"], ["implicit-model-download", "non-loopback-endpoint", "model-outside-allowlist"]),
        Seed("sindresorhus", "awesome", "Curated repository discovery index", RepositoryIntegrationMode.DiscoverySeed, "CC0-1.0", ["repository-discovery"], ["automatic-code-execution"]),
        Seed("OpenHands", "OpenHands", "Sandboxed coding-agent architecture reference and optional backend candidate", RepositoryIntegrationMode.Adapter, "MIT", ["sandboxed-agent-execution"], ["unrestricted-host-filesystem"]),
        Seed("Shubhamsaboo", "awesome-llm-apps", "Agent, RAG and multi-agent pattern/evaluation corpus", RepositoryIntegrationMode.Reference, "Apache-2.0", ["pattern-study", "benchmark-design"], ["wholesale-code-import"]),
        Seed("langflow-ai", "langflow", "Visual agent workflow, API/MCP and observability reference", RepositoryIntegrationMode.Adapter, "MIT", ["workflow-interchange", "mcp-adapter"], ["implicit-plugin-execution"]),
        Seed("punkpeye", "awesome-mcp-servers", "MCP discovery catalog", RepositoryIntegrationMode.DiscoverySeed, "MIT", ["mcp-discovery"], ["automatic-server-install", "automatic-server-execution"]),
        Seed("nexu-io", "open-design", "Local-first reconstruction/design-studio architecture reference", RepositoryIntegrationMode.Adapter, "Apache-2.0", ["sandboxed-preview", "agent-runtime-adapter"], ["unreviewed-plugin-execution"]),
        Seed("public-apis", "public-apis", "Public API discovery catalog", RepositoryIntegrationMode.DiscoverySeed, "MIT", ["api-discovery"], ["automatic-credential-use"]),
        Seed("D4Vinci", "Scrapling", "Authorized web evidence collection and resilient parsing candidate", RepositoryIntegrationMode.Adapter, "BSD-3-Clause", ["authorized-public-web-fetch", "resilient-parsing"], ["anti-bot-bypass", "captcha-bypass", "cloudflare-bypass", "access-control-evasion"]),
        Seed("ripienaar", "free-for-dev", "Infrastructure and cost-discovery reference", RepositoryIntegrationMode.Reference, null, ["service-discovery"], ["catalog-vendoring", "automatic-service-enrollment"]),
        Seed("microsoft", "mxc", "Microsoft policy-driven isolation and containment architecture reference", RepositoryIntegrationMode.Reference, "MIT", ["sandbox-architecture-study", "isolation-policy-study", "containment-benchmark-design"], ["automatic-code-execution", "automatic-build", "runtime-dependency"], MxcVerifiedAt)
    ];

    public static RepositoryIntelligenceRecord Find(string fullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        return InitialSeeds.Single(record => string.Equals(record.FullName, fullName, StringComparison.OrdinalIgnoreCase));
    }

    private static RepositoryIntelligenceRecord Seed(
        string owner,
        string name,
        string purpose,
        RepositoryIntegrationMode mode,
        string? license,
        IReadOnlyList<string> allowedCapabilities,
        IReadOnlyList<string> deniedCapabilities,
        DateTimeOffset? verifiedAt = null)
    {
        var record = new RepositoryIntelligenceRecord(
            owner,
            name,
            new Uri($"https://github.com/{owner}/{name}", UriKind.Absolute),
            purpose,
            mode,
            license,
            PinnedRevision: null,
            ContentSha256: null,
            SecurityReview: RepositorySecurityReviewState.NeedsReview,
            RuntimeAllowlisted: false,
            LastVerifiedAt: verifiedAt ?? VerifiedAt,
            AllowedCapabilities: allowedCapabilities,
            DeniedCapabilities: deniedCapabilities);

        record.Validate();
        return record;
    }
}
