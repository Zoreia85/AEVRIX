using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Core;

public sealed record WhiteLabelBranding(
    string ProductName,
    string PublisherName,
    string? LogoAssetPath,
    string? LogoAssetSha256,
    string? PrimaryColor,
    string? SecondaryColor,
    string? AccentColor)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProductName);
        ArgumentException.ThrowIfNullOrWhiteSpace(PublisherName);
        if (ProductName.Length > 120 || PublisherName.Length > 160)
        {
            throw new ArgumentException("Whitelabel product and publisher names must remain within bounded display lengths.");
        }

        if (!string.IsNullOrWhiteSpace(LogoAssetPath))
        {
            ValidateSha256(LogoAssetSha256, nameof(LogoAssetSha256));
        }
        else if (!string.IsNullOrWhiteSpace(LogoAssetSha256))
        {
            ValidateSha256(LogoAssetSha256, nameof(LogoAssetSha256));
        }

        ValidateColor(PrimaryColor, nameof(PrimaryColor));
        ValidateColor(SecondaryColor, nameof(SecondaryColor));
        ValidateColor(AccentColor, nameof(AccentColor));
    }

    private static void ValidateColor(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        if (normalized.Length is not (7 or 9) || normalized[0] != '#' ||
            !normalized[1..].All(character => Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Colors must use #RRGGBB or #AARRGGBB hexadecimal notation.", parameterName);
        }
    }

    private static void ValidateSha256(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A logo asset must be bound by a 64-character SHA-256 digest.", parameterName);
        }
    }
}

public sealed record WhiteLabelRequirementBinding(
    string RequirementId,
    IReadOnlyList<string> EvidenceIds,
    bool BehaviorRequired,
    bool OriginalExpressionForbidden)
{
    public void Validate()
    {
        WorkspaceScope.ValidateToken(RequirementId, nameof(RequirementId));
        ArgumentNullException.ThrowIfNull(EvidenceIds);
        if (EvidenceIds.Count == 0 || EvidenceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Each whitelabel requirement must be bound to at least one evidence identifier.", nameof(EvidenceIds));
        }
        if (!OriginalExpressionForbidden)
        {
            throw new InvalidOperationException("Whitelabel reconstruction must explicitly forbid copying original protected expression.");
        }
    }
}

public sealed record WhiteLabelBuildSpecification(
    string SourceWorkspaceId,
    string SourceBlueprintSha256,
    WhiteLabelBranding Branding,
    IReadOnlyList<WhiteLabelRequirementBinding> Requirements,
    bool RestrictedSourceCodeAccessed,
    bool OriginalTrademarkAssetsIncluded,
    bool OriginalSecretsIncluded)
{
    public void Validate()
    {
        WorkspaceScope.ValidateToken(SourceWorkspaceId, nameof(SourceWorkspaceId));
        ValidateSha256(SourceBlueprintSha256, nameof(SourceBlueprintSha256));
        ArgumentNullException.ThrowIfNull(Branding);
        ArgumentNullException.ThrowIfNull(Requirements);
        Branding.Validate();

        if (Requirements.Count == 0)
        {
            throw new InvalidOperationException("Whitelabel reconstruction requires evidence-bound functional requirements.");
        }
        foreach (var requirement in Requirements)
        {
            requirement.Validate();
        }

        if (RestrictedSourceCodeAccessed)
        {
            throw new InvalidOperationException("Clean-room whitelabel reconstruction cannot claim separation when restricted original source code was accessed by the implementation boundary.");
        }
        if (OriginalTrademarkAssetsIncluded)
        {
            throw new InvalidOperationException("Original trademarked branding assets must not be included in the whitelabel implementation package.");
        }
        if (OriginalSecretsIncluded)
        {
            throw new InvalidOperationException("Original credentials, tokens, keys or secrets must never be included in the whitelabel implementation package.");
        }
    }

    public string ComputeSpecificationSha256()
    {
        Validate();
        var canonical = new StringBuilder()
            .AppendLine("AEVRIX-WHITELABEL-SPEC-V2")
            .AppendLine(SourceWorkspaceId)
            .AppendLine(SourceBlueprintSha256.ToLowerInvariant())
            .AppendLine(Branding.ProductName.Trim())
            .AppendLine(Branding.PublisherName.Trim())
            .AppendLine(Branding.LogoAssetSha256?.Trim().ToLowerInvariant() ?? string.Empty)
            .AppendLine(Branding.PrimaryColor?.Trim() ?? string.Empty)
            .AppendLine(Branding.SecondaryColor?.Trim() ?? string.Empty)
            .AppendLine(Branding.AccentColor?.Trim() ?? string.Empty);

        foreach (var requirement in Requirements.OrderBy(item => item.RequirementId, StringComparer.Ordinal))
        {
            canonical.Append(requirement.RequirementId).Append('|')
                .Append(requirement.BehaviorRequired ? '1' : '0').Append('|')
                .Append(requirement.OriginalExpressionForbidden ? '1' : '0').Append('|')
                .AppendJoin(',', requirement.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal))
                .AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Expected a 64-character SHA-256 digest.", parameterName);
        }
    }
}
