using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public sealed record BlueprintEvidenceExecutionProvenance(
    string EvidenceId,
    string SourceTaskId,
    MissionSpecialistKind Specialist,
    string ExecutionId,
    string EvidenceContentSha256,
    string CompletionRecordHashSha256,
    string ResultDigestSha256)
{
    public BlueprintEvidenceExecutionProvenance Validate()
    {
        if (!MissionTaskSpec.IsSafeId(EvidenceId, 3, 160)
            || !MissionTaskSpec.IsSafeId(SourceTaskId, 3, 128)
            || !MissionTaskSpec.IsSafeId(ExecutionId, 3, 160))
        {
            throw new InvalidDataException("Blueprint execution provenance identity is invalid.");
        }

        ExecutionProofEvent.ValidateSha256(EvidenceContentSha256, nameof(EvidenceContentSha256), required: true);
        ExecutionProofEvent.ValidateSha256(CompletionRecordHashSha256, nameof(CompletionRecordHashSha256), required: true);
        ExecutionProofEvent.ValidateSha256(ResultDigestSha256, nameof(ResultDigestSha256), required: true);
        return this;
    }
}

public sealed record ProofBoundBlueprintKnowledgeRequirement(
    BlueprintKnowledgeRequirement Requirement,
    string RunId,
    ExecutionProofHead LedgerHead,
    IReadOnlyList<BlueprintEvidenceExecutionProvenance> EvidenceProvenance,
    string ClosureSha256)
{
    public ProofBoundBlueprintKnowledgeRequirement Validate()
    {
        ArgumentNullException.ThrowIfNull(Requirement);
        ArgumentNullException.ThrowIfNull(LedgerHead);
        Requirement.Validate();
        if (!MissionTaskSpec.IsSafeId(RunId, 3, 160))
        {
            throw new InvalidDataException("Proof-bound Blueprint run id is invalid.");
        }

        ExecutionProofEvent.ValidateSha256(LedgerHead.HeadHashSha256, nameof(LedgerHead.HeadHashSha256), required: true);
        ExecutionProofEvent.ValidateSha256(ClosureSha256, nameof(ClosureSha256), required: true);
        if (LedgerHead.EntryCount < 1)
        {
            throw new InvalidDataException("Proof-bound Blueprint requires a non-empty execution proof ledger.");
        }
        if (EvidenceProvenance is null
            || EvidenceProvenance.Count != Requirement.EvidenceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new InvalidDataException("Proof-bound Blueprint provenance must cover every promoted evidence id exactly once.");
        }

        foreach (var provenance in EvidenceProvenance)
        {
            provenance.Validate();
        }

        if (EvidenceProvenance.Select(item => item.EvidenceId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != EvidenceProvenance.Count)
        {
            throw new InvalidDataException("Proof-bound Blueprint provenance contains duplicate evidence ids.");
        }

        return this;
    }
}

/// <summary>
/// Closes the Evidence-to-Blueprint chain against the durable execution ledger. The binder accepts
/// only evidence whose declared source task has a successful Completed proof in the same project,
/// run, target and specialist identity, verifies the complete hash chain/head first, and seals the
/// resulting provenance map with a deterministic SHA-256 closure digest. Raw evidence content,
/// objectives, prompts, personal data and artifact bytes never enter this contract.
/// </summary>
public sealed class BlueprintExecutionProvenanceBinder
{
    private const string CapabilityClass = "mission-specialist";

    public ProofBoundBlueprintKnowledgeRequirement Bind(
        BlueprintKnowledgeRequirement requirement,
        IReadOnlyCollection<EvidenceObservation> observations,
        string runId,
        IReadOnlyList<ExecutionProofRecord> records,
        ExecutionProofHead ledgerHead)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(ledgerHead);
        requirement.Validate();
        if (!MissionTaskSpec.IsSafeId(runId, 3, 160))
        {
            throw new InvalidDataException("Blueprint provenance run id is invalid.");
        }

        ExecutionProofLedger.VerifySnapshot(records, ledgerHead);
        if (ledgerHead.EntryCount == 0)
        {
            throw new InvalidOperationException("Blueprint provenance cannot be established from an empty execution proof ledger.");
        }

        var byEvidenceId = observations
            .Select(item => item?.Validate() ?? throw new InvalidDataException("Blueprint provenance contains a null evidence observation."))
            .GroupBy(item => item.EvidenceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? group.Single()
                    : throw new InvalidDataException("Blueprint provenance contains duplicate evidence observations."),
                StringComparer.OrdinalIgnoreCase);

        var provenance = new List<BlueprintEvidenceExecutionProvenance>();
        foreach (var evidenceId in requirement.EvidenceIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byEvidenceId.TryGetValue(evidenceId, out var observation))
            {
                throw new InvalidDataException($"Blueprint provenance evidence '{evidenceId}' is missing.");
            }

            EnsureObservationBoundary(requirement, observation);
            var executionId = ComputeMissionExecutionId(
                observation.ProjectId,
                runId,
                observation.TargetId,
                observation.SourceTaskId,
                observation.Specialist);

            var completions = records
                .Where(record => record.Event.Stage == ExecutionProofStage.Completed
                    && string.Equals(record.Event.ExecutionId, executionId, StringComparison.Ordinal))
                .ToArray();
            if (completions.Length != 1)
            {
                throw new InvalidDataException("Blueprint evidence must resolve to exactly one completed source execution proof.");
            }

            var completion = completions[0];
            var proof = completion.Event;
            if (proof.ProjectId != requirement.ProjectId
                || !string.Equals(proof.RunId, runId, StringComparison.Ordinal)
                || !string.Equals(proof.CapabilityClass, CapabilityClass, StringComparison.Ordinal)
                || !string.Equals(proof.CapabilityId, observation.Specialist.ToString(), StringComparison.Ordinal)
                || proof.Outcome != ExecutionProofOutcome.Succeeded
                || proof.ResultDigestSha256 is null)
            {
                throw new InvalidOperationException("Blueprint evidence source execution is not a successful proof-bound specialist completion.");
            }

            provenance.Add(new BlueprintEvidenceExecutionProvenance(
                observation.EvidenceId,
                observation.SourceTaskId,
                observation.Specialist,
                executionId,
                observation.ContentSha256.ToLowerInvariant(),
                completion.RecordHashSha256.ToLowerInvariant(),
                proof.ResultDigestSha256.ToLowerInvariant()).Validate());
        }

        var ordered = provenance
            .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray();
        var closure = ComputeClosure(requirement, runId, ledgerHead, ordered);
        return new ProofBoundBlueprintKnowledgeRequirement(
            requirement,
            runId,
            ledgerHead,
            ordered,
            closure).Validate();
    }

    public bool VerifyClosure(ProofBoundBlueprintKnowledgeRequirement bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        bound.Validate();
        var expected = ComputeClosure(bound.Requirement, bound.RunId, bound.LedgerHead, bound.EvidenceProvenance);
        return FixedTimeHashEquals(bound.ClosureSha256, expected);
    }

    public static string ComputeMissionExecutionId(
        Guid projectId,
        string runId,
        string targetId,
        string taskId,
        MissionSpecialistKind specialist)
    {
        if (projectId == Guid.Empty
            || !MissionTaskSpec.IsSafeId(runId, 3, 160)
            || !MissionTaskSpec.IsSafeId(targetId, 2, 128)
            || !MissionTaskSpec.IsSafeId(taskId, 3, 128))
        {
            throw new InvalidDataException("Mission execution identity scope is invalid.");
        }

        return "mission-task:" + Digest([
            "aevrix-mission-execution-v1",
            projectId.ToString("D"),
            runId,
            targetId,
            taskId,
            specialist.ToString()
        ]);
    }

    private static void EnsureObservationBoundary(
        BlueprintKnowledgeRequirement requirement,
        EvidenceObservation observation)
    {
        if (observation.ProjectId != requirement.ProjectId
            || !string.Equals(observation.TargetId, requirement.TargetId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(observation.ClaimKey, requirement.ClaimKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Blueprint provenance cannot cross project, target or claim boundaries.");
        }
        if (observation.ContainsPersonalData
            || observation.Sensitivity == EvidenceSensitivity.PersonalData
            || observation.ContainsRawSecretMaterial)
        {
            throw new InvalidOperationException("Sensitive raw or personal evidence cannot enter proof-bound Blueprint provenance.");
        }
    }

    private static string ComputeClosure(
        BlueprintKnowledgeRequirement requirement,
        string runId,
        ExecutionProofHead head,
        IReadOnlyList<BlueprintEvidenceExecutionProvenance> provenance)
    {
        var parts = new List<string>
        {
            "aevrix-blueprint-provenance-closure-v1",
            requirement.RequirementId,
            requirement.ProjectId.ToString("D"),
            requirement.TargetId,
            requirement.ClaimKey,
            requirement.Statement.Trim(),
            requirement.Basis.ToString(),
            requirement.Sensitivity.ToString(),
            requirement.PromotionLevel.ToString(),
            requirement.Confidence.ToString("R", CultureInfo.InvariantCulture),
            requirement.SourceKnowledgeId,
            requirement.ValidationRecordId,
            runId,
            head.EntryCount.ToString(CultureInfo.InvariantCulture),
            head.HeadHashSha256.ToLowerInvariant()
        };
        parts.AddRange(requirement.EvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => "requirement-evidence:" + value));
        foreach (var item in provenance.OrderBy(value => value.EvidenceId, StringComparer.Ordinal))
        {
            parts.Add("evidence:" + item.EvidenceId);
            parts.Add("source-task:" + item.SourceTaskId);
            parts.Add("specialist:" + item.Specialist);
            parts.Add("execution:" + item.ExecutionId);
            parts.Add("content:" + item.EvidenceContentSha256.ToLowerInvariant());
            parts.Add("completion-record:" + item.CompletionRecordHashSha256.ToLowerInvariant());
            parts.Add("result:" + item.ResultDigestSha256.ToLowerInvariant());
        }
        return Digest(parts);
    }

    private static string Digest(IEnumerable<string> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part ?? string.Empty);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool FixedTimeHashEquals(string left, string right)
    {
        ExecutionProofEvent.ValidateSha256(left, nameof(left), required: true);
        ExecutionProofEvent.ValidateSha256(right, nameof(right), required: true);
        var a = Convert.FromHexString(left);
        var b = Convert.FromHexString(right);
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
}
