using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Core;

public enum BlueprintKnowledgeExchangeBasis
{
    Observed,
    ExperimentallyValidated,
    Inferred,
    VendorClaim
}

public enum BlueprintKnowledgeExchangePromotion
{
    Conditional,
    Reconstructable
}

public sealed record ImportedBlueprintKnowledgeRequirement(
    string RequirementId,
    Guid ProjectId,
    string TargetId,
    string ClaimKey,
    string Statement,
    BlueprintKnowledgeExchangeBasis Basis,
    BlueprintKnowledgeExchangePromotion Promotion,
    double Confidence,
    IReadOnlyList<string> EvidenceIds,
    string SourceKnowledgeId,
    string ValidationRecordId,
    string PayloadSha256)
{
    public bool CanDriveReconstruction => Promotion == BlueprintKnowledgeExchangePromotion.Reconstructable;
}

public sealed class BlueprintKnowledgeExchangeImporter
{
    public const int CurrentSchemaVersion = 1;
    private const int MaxDocumentBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public ImportedBlueprintKnowledgeRequirement Import(
        ReadOnlySpan<byte> utf8Json,
        Guid expectedProjectId,
        string expectedTargetId)
    {
        if (expectedProjectId == Guid.Empty)
        {
            throw new ArgumentException("Expected project id cannot be empty.", nameof(expectedProjectId));
        }
        ValidateId(expectedTargetId, 2, 128, nameof(expectedTargetId));
        if (utf8Json.Length is < 2 or > MaxDocumentBytes)
        {
            throw new InvalidDataException("Blueprint knowledge exchange document size is invalid.");
        }

        ExchangeEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ExchangeEnvelope>(utf8Json, JsonOptions)
                ?? throw new InvalidDataException("Blueprint knowledge exchange document is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Blueprint knowledge exchange JSON is invalid.", ex);
        }

        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported blueprint knowledge exchange schema version {envelope.SchemaVersion}.");
        }
        var requirement = envelope.Requirement
            ?? throw new InvalidDataException("Blueprint knowledge exchange requirement is missing.");
        ValidateRequirement(requirement);

        if (requirement.ProjectId != expectedProjectId
            || !string.Equals(requirement.TargetId, expectedTargetId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Blueprint knowledge exchange scope does not match the local project and target.");
        }
        if (!string.Equals(requirement.Sensitivity, "Public", StringComparison.Ordinal)
            && !string.Equals(requirement.Sensitivity, "ProjectConfidential", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Personal or unknown sensitivity cannot enter the local Blueprint exchange boundary.");
        }

        var canonical = Canonicalize(requirement);
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        if (!FixedTimeEqualsHex(expectedHash, envelope.PayloadSha256))
        {
            throw new InvalidDataException("Blueprint knowledge exchange payload hash verification failed.");
        }

        if (!Enum.TryParse<BlueprintKnowledgeExchangeBasis>(requirement.Basis, ignoreCase: false, out var basis)
            || !Enum.TryParse<BlueprintKnowledgeExchangePromotion>(requirement.PromotionLevel, ignoreCase: false, out var promotion))
        {
            throw new InvalidDataException("Blueprint knowledge exchange basis or promotion level is unknown.");
        }

        return new ImportedBlueprintKnowledgeRequirement(
            requirement.RequirementId,
            requirement.ProjectId,
            requirement.TargetId,
            requirement.ClaimKey,
            requirement.Statement.Trim(),
            basis,
            promotion,
            requirement.Confidence,
            requirement.EvidenceIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            requirement.SourceKnowledgeId,
            requirement.ValidationRecordId,
            expectedHash);
    }

    public IReadOnlyList<ImportedBlueprintKnowledgeRequirement> ImportSet(
        IEnumerable<ReadOnlyMemory<byte>> documents,
        Guid expectedProjectId,
        string expectedTargetId)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var byId = new Dictionary<string, ImportedBlueprintKnowledgeRequirement>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var imported = Import(document.Span, expectedProjectId, expectedTargetId);
            if (byId.TryGetValue(imported.RequirementId, out var existing))
            {
                if (!string.Equals(existing.PayloadSha256, imported.PayloadSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Blueprint knowledge requirement id was rebound to different content.");
                }
                continue;
            }
            byId.Add(imported.RequirementId, imported);
        }
        return byId.Values.OrderBy(x => x.RequirementId, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateRequirement(ExchangeRequirement requirement)
    {
        ValidateId(requirement.RequirementId, 3, 160, nameof(requirement.RequirementId));
        if (requirement.ProjectId == Guid.Empty)
        {
            throw new InvalidDataException("Blueprint knowledge requirement project id is empty.");
        }
        ValidateId(requirement.TargetId, 2, 128, nameof(requirement.TargetId));
        ValidateId(requirement.ClaimKey, 3, 160, nameof(requirement.ClaimKey));
        ValidateId(requirement.SourceKnowledgeId, 3, 160, nameof(requirement.SourceKnowledgeId));
        ValidateId(requirement.ValidationRecordId, 3, 160, nameof(requirement.ValidationRecordId));
        if (string.IsNullOrWhiteSpace(requirement.Statement) || requirement.Statement.Length > 64_000)
        {
            throw new InvalidDataException("Blueprint knowledge requirement statement is invalid.");
        }
        if (!double.IsFinite(requirement.Confidence) || requirement.Confidence is < 0 or > 1)
        {
            throw new InvalidDataException("Blueprint knowledge requirement confidence is outside [0,1].");
        }
        if (requirement.EvidenceIds is null || requirement.EvidenceIds.Count is < 1 or > 2_000)
        {
            throw new InvalidDataException("Blueprint knowledge requirement evidence is invalid.");
        }
        foreach (var id in requirement.EvidenceIds)
        {
            ValidateId(id, 3, 160, "evidenceId");
        }
        if (requirement.EvidenceIds.Distinct(StringComparer.Ordinal).Count() != requirement.EvidenceIds.Count)
        {
            throw new InvalidDataException("Blueprint knowledge requirement contains duplicate evidence ids.");
        }
    }

    internal static string Canonicalize(ExchangeRequirement requirement)
    {
        var fields = new[]
        {
            requirement.RequirementId,
            requirement.ProjectId.ToString("D"),
            requirement.TargetId,
            requirement.ClaimKey,
            requirement.Statement.Trim(),
            requirement.Basis,
            requirement.Sensitivity,
            requirement.PromotionLevel,
            requirement.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string.Join("|", requirement.EvidenceIds.OrderBy(x => x, StringComparer.Ordinal)),
            requirement.SourceKnowledgeId,
            requirement.ValidationRecordId
        };
        return string.Concat(fields.Select(value => $"{Encoding.UTF8.GetByteCount(value)}:{value}"));
    }

    private static bool FixedTimeEqualsHex(string expected, string supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length != 64 || supplied.Any(ch => !Uri.IsHexDigit(ch)))
        {
            return false;
        }

        var a = Convert.FromHexString(expected);
        var b = Convert.FromHexString(supplied);
        try
        {
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(a);
            CryptographicOperations.ZeroMemory(b);
        }
    }

    private static void ValidateId(string value, int min, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length < min
            || value.Length > max
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':')))
        {
            throw new InvalidDataException($"Blueprint knowledge exchange {name} is invalid.");
        }
    }

    internal sealed record ExchangeEnvelope(int SchemaVersion, ExchangeRequirement? Requirement, string PayloadSha256);

    internal sealed record ExchangeRequirement(
        string RequirementId,
        Guid ProjectId,
        string TargetId,
        string ClaimKey,
        string Statement,
        string Basis,
        string Sensitivity,
        string PromotionLevel,
        double Confidence,
        IReadOnlyList<string> EvidenceIds,
        string SourceKnowledgeId,
        string ValidationRecordId);
}
