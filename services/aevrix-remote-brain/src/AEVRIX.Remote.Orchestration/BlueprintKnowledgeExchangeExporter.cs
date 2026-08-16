using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Remote.Orchestration;

public sealed class BlueprintKnowledgeExchangeExporter
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public byte[] Export(ProofBoundBlueprintKnowledgeRequirement boundRequirement)
    {
        ArgumentNullException.ThrowIfNull(boundRequirement);
        BlueprintExecutionProvenanceBinder.Verify(boundRequirement);

        var requirement = boundRequirement.Requirement;
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

        var provenance = new ExchangeProvenance(
            boundRequirement.MissionId,
            boundRequirement.LedgerHead.EntryCount,
            boundRequirement.LedgerHead.HeadHashSha256.ToLowerInvariant(),
            boundRequirement.ProvenanceDigestSha256.ToLowerInvariant(),
            boundRequirement.EvidenceExecutionProofs
                .OrderBy(static x => x.EvidenceId, StringComparer.Ordinal)
                .Select(static x => new ExchangeEvidenceProof(
                    x.EvidenceId,
                    x.SourceTaskId,
                    x.Specialist.ToString(),
                    x.ExecutionId,
                    x.CompletedRecordHashSha256.ToLowerInvariant(),
                    x.ResultDigestSha256.ToLowerInvariant()))
                .ToArray());

        var canonical = Canonicalize(dto, provenance);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return JsonSerializer.SerializeToUtf8Bytes(
            new ExchangeEnvelope(CurrentSchemaVersion, dto, provenance, hash),
            JsonOptions);
    }

    private static string Canonicalize(ExchangeRequirement requirement, ExchangeProvenance provenance)
    {
        var fields = new List<string>
        {
            "aevrix-blueprint-knowledge-exchange-v2",
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
            requirement.ValidationRecordId,
            provenance.MissionId,
            provenance.LedgerEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            provenance.LedgerHeadHashSha256,
            provenance.ProvenanceDigestSha256
        };

        foreach (var proof in provenance.EvidenceExecutionProofs.OrderBy(static x => x.EvidenceId, StringComparer.Ordinal))
        {
            fields.Add(proof.EvidenceId);
            fields.Add(proof.SourceTaskId);
            fields.Add(proof.Specialist);
            fields.Add(proof.ExecutionId);
            fields.Add(proof.CompletedRecordHashSha256);
            fields.Add(proof.ResultDigestSha256);
        }

        return string.Concat(fields.Select(value => $"{Encoding.UTF8.GetByteCount(value)}:{value}"));
    }

    private sealed record ExchangeEnvelope(
        int SchemaVersion,
        ExchangeRequirement Requirement,
        ExchangeProvenance Provenance,
        string PayloadSha256);

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

    private sealed record ExchangeProvenance(
        string MissionId,
        long LedgerEntryCount,
        string LedgerHeadHashSha256,
        string ProvenanceDigestSha256,
        IReadOnlyList<ExchangeEvidenceProof> EvidenceExecutionProofs);

    private sealed record ExchangeEvidenceProof(
        string EvidenceId,
        string SourceTaskId,
        string Specialist,
        string ExecutionId,
        string CompletedRecordHashSha256,
        string ResultDigestSha256);
}
