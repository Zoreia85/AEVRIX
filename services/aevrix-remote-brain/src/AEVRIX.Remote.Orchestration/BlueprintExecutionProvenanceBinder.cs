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
    string ProvenanceDigestSha256);

/// <summary>
/// Binds reconstructable Blueprint evidence to successful, governed specialist executions
/// in one cryptographically verified execution-proof snapshot. Raw evidence and PII are excluded.
/// </summary>
public sealed class BlueprintExecutionProvenanceBinder
{
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
        var ids = requirement.EvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
        var byId = observations
            .GroupBy(static x => x.EvidenceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (byId.Count != ids.Length
            || ids.Any(id => !byId.TryGetValue(id, out var matches) || matches.Length != 1)
            || byId.Keys.Any(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Blueprint provenance evidence set is not an exact one-to-one match.");

        var bindings = new List<BlueprintEvidenceExecutionProof>(ids.Length);
        foreach (var id in ids)
        {
            var observation = byId[id][0].Validate();
            if (observation.ProjectId != requirement.ProjectId
                || !string.Equals(observation.TargetId, requirement.TargetId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(observation.ClaimKey, requirement.ClaimKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Blueprint provenance cannot cross project, target or claim boundaries.");
            if (observation.ContainsPersonalData || observation.Sensitivity == EvidenceSensitivity.PersonalData)
                throw new InvalidOperationException("Personal data cannot enter Blueprint execution provenance.");

            var executionId = MissionExecutionProofIdentity.CreateExecutionId(
                requirement.ProjectId, missionId, requirement.TargetId,
                observation.SourceTaskId, observation.Specialist);
            var completed = proofRecords.Where(record =>
                    string.Equals(record.Event.ExecutionId, executionId, StringComparison.Ordinal)
                    && record.Event.Stage == ExecutionProofStage.Completed)
                .ToArray();
            if (completed.Length != 1)
                throw new InvalidDataException("Blueprint evidence does not resolve to exactly one completed execution proof.");

            var proof = completed[0];
            if (proof.Event.ProjectId != requirement.ProjectId
                || !string.Equals(proof.Event.RunId, missionId, StringComparison.Ordinal)
                || !string.Equals(proof.Event.CapabilityClass, "mission-specialist", StringComparison.Ordinal)
                || !string.Equals(proof.Event.CapabilityId, observation.Specialist.ToString(), StringComparison.Ordinal)
                || proof.Event.Outcome != ExecutionProofOutcome.Succeeded
                || proof.Event.ResultDigestSha256 is null)
                throw new InvalidDataException("Blueprint evidence is not backed by a successful governed specialist execution.");

            bindings.Add(new(id, observation.SourceTaskId, observation.Specialist, executionId,
                proof.RecordHashSha256, proof.Event.ResultDigestSha256));
        }

        var digest = ComputeDigest(requirement, missionId, expectedHead, bindings);
        var bound = new ProofBoundBlueprintKnowledgeRequirement(requirement, missionId, expectedHead, bindings, digest);
        Verify(bound);
        return bound;
    }

    public static void Verify(ProofBoundBlueprintKnowledgeRequirement bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(bound.Requirement);
        ArgumentNullException.ThrowIfNull(bound.LedgerHead);
        ArgumentNullException.ThrowIfNull(bound.EvidenceExecutionProofs);
        bound.Requirement.Validate();

        if (!MissionTaskSpec.IsSafeId(bound.MissionId, 3, 128))
            throw new InvalidDataException("Blueprint provenance mission id is invalid.");
        ExecutionProofEvent.ValidateSha256(bound.LedgerHead.HeadHashSha256, nameof(bound.LedgerHead.HeadHashSha256), required: true);
        if (bound.LedgerHead.EntryCount <= 0)
            throw new InvalidDataException("Blueprint provenance ledger head must contain at least one entry.");
        ExecutionProofEvent.ValidateSha256(bound.ProvenanceDigestSha256, nameof(bound.ProvenanceDigestSha256), required: true);

        var expectedEvidenceIds = bound.Requirement.EvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
        var proofs = bound.EvidenceExecutionProofs.ToArray();
        if (proofs.Length != expectedEvidenceIds.Length)
            throw new InvalidDataException("Blueprint provenance proof set does not match the requirement evidence set.");

        var actualEvidenceIds = proofs
            .Select(static x => x.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
        if (!expectedEvidenceIds.SequenceEqual(actualEvidenceIds, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Blueprint provenance proof set does not exactly match the requirement evidence set.");

        foreach (var proof in proofs)
        {
            if (!MissionTaskSpec.IsSafeId(proof.EvidenceId, 3, 160)
                || !MissionTaskSpec.IsSafeId(proof.SourceTaskId, 3, 160)
                || !MissionTaskSpec.IsSafeId(proof.ExecutionId, 3, 160)
                || !Enum.IsDefined(proof.Specialist))
                throw new InvalidDataException("Blueprint provenance proof identity is invalid.");
            ExecutionProofEvent.ValidateSha256(proof.CompletedRecordHashSha256, nameof(proof.CompletedRecordHashSha256), required: true);
            ExecutionProofEvent.ValidateSha256(proof.ResultDigestSha256, nameof(proof.ResultDigestSha256), required: true);

            var expectedExecutionId = MissionExecutionProofIdentity.CreateExecutionId(
                bound.Requirement.ProjectId,
                bound.MissionId,
                bound.Requirement.TargetId,
                proof.SourceTaskId,
                proof.Specialist);
            if (!string.Equals(expectedExecutionId, proof.ExecutionId, StringComparison.Ordinal))
                throw new InvalidDataException("Blueprint provenance execution identity is inconsistent.");
        }

        var expectedDigest = ComputeDigest(bound.Requirement, bound.MissionId, bound.LedgerHead, proofs);
        var expectedBytes = Convert.FromHexString(expectedDigest);
        var suppliedBytes = Convert.FromHexString(bound.ProvenanceDigestSha256);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
                throw new CryptographicException("Blueprint provenance digest verification failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(suppliedBytes);
        }
    }

    internal static string ComputeDigest(
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
        foreach (var item in bindings.OrderBy(static x => x.EvidenceId, StringComparer.Ordinal))
        {
            fields.Add(item.EvidenceId);
            fields.Add(item.SourceTaskId);
            fields.Add(item.Specialist.ToString());
            fields.Add(item.ExecutionId);
            fields.Add(item.CompletedRecordHashSha256.ToLowerInvariant());
            fields.Add(item.ResultDigestSha256.ToLowerInvariant());
        }

        var canonical = string.Concat(fields.Select(value => $"{Encoding.UTF8.GetByteCount(value)}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
