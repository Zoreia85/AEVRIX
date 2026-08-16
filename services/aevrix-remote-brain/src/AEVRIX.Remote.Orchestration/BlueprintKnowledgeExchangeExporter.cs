using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Remote.Orchestration;

public sealed class BlueprintKnowledgeExchangeExporter
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public byte[] Export(BlueprintKnowledgeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        requirement.Validate();
        if (requirement.Sensitivity == EvidenceSensitivity.PersonalData)
        {
            throw new InvalidOperationException("Personal data cannot cross the Blueprint knowledge exchange boundary.");
        }

        var dto = new ExchangeRequirement(
            requirement.RequirementId,
            requirement.ProjectId,
            requirement.TargetId,
            requirement.ClaimKey,
            requirement.Statement.Trim(),
            requirement.Basis.ToString(),
            requirement.Sensitivity.ToString(),
            requirement.PromotionLevel.ToString(),
            requirement.Confidence,
            requirement.EvidenceIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            requirement.SourceKnowledgeId,
            requirement.ValidationRecordId);

        var canonical = Canonicalize(dto);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return JsonSerializer.SerializeToUtf8Bytes(
            new ExchangeEnvelope(CurrentSchemaVersion, dto, hash),
            JsonOptions);
    }

    private static string Canonicalize(ExchangeRequirement requirement)
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

    private sealed record ExchangeEnvelope(int SchemaVersion, ExchangeRequirement Requirement, string PayloadSha256);

    private sealed record ExchangeRequirement(
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
