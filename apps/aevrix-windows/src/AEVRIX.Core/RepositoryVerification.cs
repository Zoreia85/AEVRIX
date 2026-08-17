namespace Aevrix.Core;

public enum RepositoryVerificationSeverity
{
    Info,
    Warning,
    Blocker
}

public sealed record RepositoryObservation(
    string FullName,
    Uri CanonicalUrl,
    string DefaultBranch,
    string HeadRevision,
    string? SpdxLicense,
    bool Archived,
    DateTimeOffset ObservedAt,
    string? ContentSha256)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FullName);
        ArgumentNullException.ThrowIfNull(CanonicalUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(HeadRevision);

        if (ObservedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(ObservedAt));
        }

        if (!string.Equals(CanonicalUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(CanonicalUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(CanonicalUrl.Query)
            || !string.IsNullOrEmpty(CanonicalUrl.Fragment))
        {
            throw new ArgumentException("Observed repository URL must be a clean HTTPS github.com URL.", nameof(CanonicalUrl));
        }

        if (!IsExactGitRevision(HeadRevision))
        {
            throw new ArgumentException("Observed head revision must be a full 40- or 64-character hexadecimal Git revision.", nameof(HeadRevision));
        }

        if (ContentSha256 is not null && !IsHex(ContentSha256, 64, 64))
        {
            throw new ArgumentException("Observed content hash must be SHA-256 hexadecimal when supplied.", nameof(ContentSha256));
        }
    }

    private static bool IsExactGitRevision(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static bool IsHex(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength
        && value.Length <= maximumLength
        && value.All(Uri.IsHexDigit);
}

public sealed record RepositoryVerificationFinding(
    string Code,
    RepositoryVerificationSeverity Severity,
    string Message);

public sealed record RepositoryVerificationReport(
    string RepositoryFullName,
    DateTimeOffset VerifiedAt,
    IReadOnlyList<RepositoryVerificationFinding> Findings)
{
    public bool HasBlockers => Findings.Any(finding => finding.Severity == RepositoryVerificationSeverity.Blocker);
    public bool CanRemainRuntimeEligible => !HasBlockers
        && Findings.All(finding => finding.Code != "repository.non-executable-by-design");
}

public static class RepositoryProvenanceVerifier
{
    public static RepositoryVerificationReport Verify(
        RepositoryIntelligenceRecord expected,
        RepositoryObservation observed,
        DateTimeOffset verifiedAt)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);
        expected.Validate();
        observed.Validate();

        if (verifiedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(verifiedAt));
        }

        var findings = new List<RepositoryVerificationFinding>();

        if (!string.Equals(expected.FullName, observed.FullName, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Blocker("repository.identity.mismatch", "Observed repository identity differs from the governed record."));
        }

        if (!UriEquals(expected.CanonicalUrl, observed.CanonicalUrl))
        {
            findings.Add(Blocker("repository.url.mismatch", "Observed canonical URL differs from the governed record."));
        }

        if (observed.Archived)
        {
            findings.Add(Blocker("repository.archived", "Archived repositories cannot remain runtime-eligible without a new review."));
        }

        if (string.IsNullOrWhiteSpace(observed.SpdxLicense))
        {
            findings.Add(Blocker("repository.license.missing", "Observed repository does not expose an SPDX license."));
        }
        else if (!string.IsNullOrWhiteSpace(expected.SpdxLicense)
                 && !string.Equals(expected.SpdxLicense, observed.SpdxLicense, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Blocker("repository.license.drift", "Observed SPDX license differs from the governed record."));
        }

        if (!string.IsNullOrWhiteSpace(expected.PinnedRevision)
            && !string.Equals(expected.PinnedRevision, observed.HeadRevision, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new RepositoryVerificationFinding(
                "repository.revision.drift",
                RepositoryVerificationSeverity.Warning,
                "Upstream HEAD differs from the governed pinned revision; automatic promotion is not allowed."));
        }

        if (!string.IsNullOrWhiteSpace(expected.ContentSha256))
        {
            if (string.IsNullOrWhiteSpace(observed.ContentSha256))
            {
                findings.Add(Blocker("repository.content-hash.missing", "Governed runtime records require an observed content hash."));
            }
            else if (!string.Equals(expected.ContentSha256, observed.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Blocker("repository.content-hash.mismatch", "Observed source content hash differs from the governed hash."));
            }
        }

        var integrationModes = expected.EffectiveIntegrationModes;
        var hasExecutableMode = integrationModes.Any(IsExecutableMode);
        var hasDenyingMode = integrationModes.Contains(RepositoryIntegrationMode.DiscoverySeed)
            || integrationModes.Contains(RepositoryIntegrationMode.Blocked);

        if (hasExecutableMode)
        {
            if (expected.GovernanceAuthority != RepositoryGovernanceAuthority.AuditedManifest)
            {
                findings.Add(Blocker("repository.authority.required", "Executable integration requires the audited canonical manifest as its governance authority."));
            }

            if (!string.Equals(expected.ManifestRuntimeApproval, "Approved", StringComparison.Ordinal))
            {
                findings.Add(Blocker("repository.manifest-runtime-approval.required", "Executable integration requires an explicit Approved runtime decision from the audited manifest."));
            }

            if (hasDenyingMode)
            {
                findings.Add(Blocker("repository.integration-mode.denied", "Discovery or blocked integration modes cannot be collapsed into executable permission."));
            }

            if (expected.SecurityReview != RepositorySecurityReviewState.Approved)
            {
                findings.Add(Blocker("repository.security-review.required", "Executable integration requires an approved security review."));
            }

            if (!expected.RuntimeAllowlisted)
            {
                findings.Add(Blocker("repository.runtime-allowlist.required", "Executable integration requires explicit runtime allowlisting."));
            }

            if (string.IsNullOrWhiteSpace(expected.PinnedRevision))
            {
                findings.Add(Blocker("repository.pin.required", "Executable integration requires a pinned Git revision."));
            }

            if (string.IsNullOrWhiteSpace(expected.ContentSha256))
            {
                findings.Add(Blocker("repository.hash.required", "Executable integration requires a governed SHA-256 source hash."));
            }
        }

        if (!hasExecutableMode)
        {
            findings.Add(new RepositoryVerificationFinding(
                "repository.non-executable-by-design",
                RepositoryVerificationSeverity.Info,
                "Reference, discovery and blocked records remain non-executable regardless of upstream health."));
        }

        if (findings.Count == 0)
        {
            findings.Add(new RepositoryVerificationFinding(
                "repository.provenance.verified",
                RepositoryVerificationSeverity.Info,
                "Repository identity, license and governed provenance checks passed."));
        }

        return new RepositoryVerificationReport(expected.FullName, verifiedAt, findings);
    }

    private static bool IsExecutableMode(RepositoryIntegrationMode mode) =>
        mode is RepositoryIntegrationMode.Adapter
            or RepositoryIntegrationMode.OptionalTool
            or RepositoryIntegrationMode.Vendored;

    private static bool UriEquals(Uri left, Uri right) =>
        string.Equals(left.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped).TrimEnd('/'),
            right.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped).TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static RepositoryVerificationFinding Blocker(string code, string message) =>
        new(code, RepositoryVerificationSeverity.Blocker, message);
}
