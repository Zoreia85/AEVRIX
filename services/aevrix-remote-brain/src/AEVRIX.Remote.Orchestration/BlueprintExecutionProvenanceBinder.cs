using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public sealed record BlueprintEvidenceExecutionProof(
    string EvidenceId,
    string SourceTaskId,
    MissionSpecialistKind Specialist,
    string ExecutionId,
    string CompletedRecordHashSha256,
    string ResultDigestSha256);

public sealed record ProofBoundBlueprintKnowledgeRequirement(
    BlueprintKnowledgeRequirement Requirement,
    string MissionId,
    ExecutionProofHead LedgerHead,
    IReadOnlyList<BlueprintEvidenceExecutionProof> EvidenceExecutionProofs,
    string ProvenanceDigestSha256)
{
    public ProofBoundBlueprintKnowledgeRequirement Validate()
    {
        Requirement.Validate();
        if (!MissionTaskSpec.IsSafeId(MissionId, 3, 128))
            throw new InvalidDataException("Blueprint provenance mission id is invalid.");
        if (LedgerHead.EntryCount < 1)
            throw new InvalidDataException("Blueprint provenance requires a non-empty execution proof head.");
        ExecutionProofEvent.ValidateSha256(LedgerHead.HeadHashSha256, nameof(LedgerHead.HeadHashSha256), required: true);
        ExecutionProofEvent.ValidateSha256(ProvenanceDigestSha256, nameof(ProvenanceDigestSha256), required: true);
        if (EvidenceExecutionProofs is null
            || EvidenceExecutionProofs.Count != Requirement.EvidenceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidDataException("Blueprint provenance must bind every evidence id exactly once.");
        return this;
    }
}

/// <summary>
/// Closes Evidence -> Blueprint provenance against a verified execution-proof ledger snapshot.
/// No raw evidence content, PII, prompts, credentials or artifact contents enter the binding.
/// </summary>
public sealed class BlueprintExecutionProvenanceBinder
{
    private const string CapabilityClass = "mission-specialist";

    public ProofBoundBlueprintKnowledgeRequirement Bind(
        BlueprintKnowledgeRequirement requirement,
        string missionId,
        IReadOnlyCollection<EvidenceObservation> observations,
        IReadOnlyList<ExecutionProofRecord> proofRecords,
        ExecutionProofHead expectedHead)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(proofRecords);
        ArgumentNullException.ThrowIfNull(expectedHead);
        requirement.Validate();

        if (!MissionTaskSpec.IsSafeId(missionId, 3, 128))
            throw new InvalidDataException("Blueprint provenance mission id is invalid.");

        ExecutionProofLedger.VerifySnapshot(proofRecords, expectedHead);

        var requiredIds = requirement.EvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var byEvidenceId = observations
            .GroupBy(static item => item.EvidenceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        if (byEvidenceId.Count != requiredIds.Length
            || requiredIds.Any(id => !byEvidenceId.TryGetValue(id, out var matches) || matches.Length != 1)
            || byEvidenceId.Keys.Any(id => !requiredIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Blueprint provenance evidence set is not an exact one-to-one match.");
        }

        var bindings = new List<BlueprintEvidenceExecutionProof>(requiredIds.Length);
        foreach (var evidenceId in requiredIds)
        {
            var observation = byEvidenceId[evidenceId][0].Validate();
            if (observation.ProjectId != requirement.ProjectId
                || !string.Equals(observation.TargetId, requirement.TargetId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(observation.ClaimKey, requirement.ClaimKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Blueprint provenance cannot cross project, target or claim boundaries.");
            }

            if (observation.ContainsPersonalData || observation.Sensitivity == EvidenceSensitivity.PersonalData)
                throw new InvalidOperationException("Personal data cannot enter Blueprint execution provenance.");

            var executionId = MissionExecutionProofIdentity.CreateExecutionId(
                requirement.ProjectId,
                missionId,
                requirement.TargetId,
                observation.SourceTaskId,
                observation.Specialist);

            var completed = proofRecords
                .Where(record => string.Equals(record.Event.ExecutionId, executionId, StringComparison.Ordinal)
                    && record.Event.Stage == ExecutionProofStage.Completed)
                .ToArray();
            if (completed.Length != 1)
                throw new InvalidDataException("Blueprint evidence does not resolve to exactly one completed execution proof.");

            var proof = completed[0];
            if (proof.Event.ProjectId != requirement.ProjectId
                || !string.Equals(proof.Event.RunId, missionId, StringComparison.Ordinal)
                || !string.Equals(proof.Event.CapabilityClass, CapabilityClass, StringComparison.Ordinal)
                || !string.Equals(proof.Event.CapabilityId, observation.Specialist.ToString(), StringComparison.Ordinal)
                || proof.Event.Outcome != ExecutionProofOutcome.Succeeded
                || proof.Event.ResultDigestSha256 is null)
            {
                throw new InvalidDataException("Blueprint evidence execution proof is not a successful governed specialist execution.");
            }

            bindings.Add(new BlueprintEvidenceExecutionProof(
                evidenceId,
                observation.SourceTaskId,
                observation.Specialist,
                executionId,
                proof.RecordHashSha256,
                proof.Event.ResultDigestSha256));
        }

        var digest = ComputeDigest(requirement, missionId, expectedHead, bindings);
        return new ProofBoundBlueprintKnowledgeRequirement(
            requirement,
            missionId,
            expectedHead,
            bindings,
            digest).Validate();
    }

    private static string ComputeDigest(
        BlueprintKnowledgeRequirement requirement,
        string missionId,
        ExecutionProofHead head,
        IReadOnlyList<BlueprintEvidenceExecutionProof> bindings)
    {
        var fields = new List<string>
        {
            "aevrix-blueprint-execution-provenance-v1",
            requirement.RequirementId,
            requirement.ProjectId.ToString("D"),
            requirement.TargetId,
            requirement.ClaimKey,
            missionId,
            head.EntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            head.HeadHashSha256.ToLowerInvariant()
        };
        foreach (var binding in bindings.OrderBy(static item => item.EvidenceId, StringComparer.Ordinal))
        {
            fields.Add(binding.EvidenceId);
            fields.Add(binding.SourceTaskId);
            fields.Add(binding.Specialist.ToString());
            fields.Add(binding.ExecutionId);
            fields.Add(binding.CompletedRecordHashSha256.ToLowerInvariant());
            fields.Add(binding.ResultDigestSha256.ToLowerInvariant());
        }

        var canonical = string.Concat(fields.Select(value => $"{Encoding.UTF8.GetByteCount(value)}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
