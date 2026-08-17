using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public sealed record MemoryAdmissionContext(
    string MissionId,
    IReadOnlyCollection<EvidenceObservation> Observations,
    IReadOnlyList<ExecutionProofRecord> ProofRecords,
    ExecutionProofHead ExpectedHead)
{
    public MemoryAdmissionContext Validate()
    {
        if (!MissionTaskSpec.IsSafeId(MissionId, 3, 128))
            throw new InvalidDataException("Memory admission mission id is invalid.");
        ArgumentNullException.ThrowIfNull(Observations);
        ArgumentNullException.ThrowIfNull(ProofRecords);
        ArgumentNullException.ThrowIfNull(ExpectedHead);
        if (Observations.Count is < 1 or > 2_000)
            throw new InvalidDataException("Memory admission observation count is invalid.");
        ExecutionProofLedger.VerifySnapshot(ProofRecords, ExpectedHead);
        return this;
    }
}

/// <summary>
/// Opaque capability required by project-knowledge repositories before committing Trusted state.
/// The constructor is internal so callers outside the orchestration trust boundary cannot mint one.
/// </summary>
public sealed class TrustedKnowledgeAdmissionAuthorization
{
    internal TrustedKnowledgeAdmissionAuthorization(
        string knowledgeId,
        string validationRecordId,
        Guid projectId,
        string targetId,
        string missionId,
        ExecutionProofHead ledgerHead,
        string admissionDigestSha256,
        DateTimeOffset admittedAt)
    {
        KnowledgeId = knowledgeId;
        ValidationRecordId = validationRecordId;
        ProjectId = projectId;
        TargetId = targetId;
        MissionId = missionId;
        LedgerHead = ledgerHead;
        AdmissionDigestSha256 = admissionDigestSha256;
        AdmittedAt = admittedAt;
    }

    public string KnowledgeId { get; }
    public string ValidationRecordId { get; }
    public Guid ProjectId { get; }
    public string TargetId { get; }
    public string MissionId { get; }
    public ExecutionProofHead LedgerHead { get; }
    public string AdmissionDigestSha256 { get; }
    public DateTimeOffset AdmittedAt { get; }

    internal TrustedKnowledgeAdmissionAuthorization Validate()
    {
        if (!MissionTaskSpec.IsSafeId(KnowledgeId, 3, 160)
            || !MissionTaskSpec.IsSafeId(ValidationRecordId, 3, 160)
            || ProjectId == Guid.Empty
            || !MissionTaskSpec.IsSafeId(TargetId, 2, 128)
            || !MissionTaskSpec.IsSafeId(MissionId, 3, 128)
            || LedgerHead is null
            || LedgerHead.EntryCount <= 0
            || AdmittedAt == default)
            throw new InvalidDataException("Trusted knowledge admission authorization is invalid.");
        ExecutionProofEvent.ValidateSha256(LedgerHead.HeadHashSha256, nameof(LedgerHead.HeadHashSha256), required: true);
        ExecutionProofEvent.ValidateSha256(AdmissionDigestSha256, nameof(AdmissionDigestSha256), required: true);
        return this;
    }
}

internal sealed record MemoryAdmissionEvidenceProof(
    string EvidenceId,
    string SourceTaskId,
    MissionSpecialistKind Specialist,
    string ExecutionId,
    string CompletedRecordHashSha256,
    string ResultDigestSha256);

internal interface IMemoryAdmissionGate
{
    Task<TrustedKnowledgeAdmissionAuthorization> AuthorizeTrustedAsync(
        CandidateKnowledge candidate,
        KnowledgeValidationRecord validation,
        MemoryAdmissionContext context,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default);
}

internal sealed class ProjectKnowledgeAdmissionGate : IMemoryAdmissionGate
{
    private readonly IExecutionProofHeadAnchor _headAnchor;

    public ProjectKnowledgeAdmissionGate(IExecutionProofHeadAnchor headAnchor) =>
        _headAnchor = headAnchor ?? throw new ArgumentNullException(nameof(headAnchor));

    public async Task<TrustedKnowledgeAdmissionAuthorization> AuthorizeTrustedAsync(
        CandidateKnowledge candidate,
        KnowledgeValidationRecord validation,
        MemoryAdmissionContext context,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (admittedAt == default) throw new ArgumentOutOfRangeException(nameof(admittedAt));
        if (candidate.TrustState != KnowledgeTrustState.Candidate)
            throw new InvalidOperationException("Only Candidate knowledge may enter trusted memory admission.");

        validation.Validate();
        context.Validate();
        var anchoredHead = await _headAnchor.LoadAsync(candidate.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Trusted memory admission requires an external monotonic execution-proof head anchor.");
        if (anchoredHead != context.ExpectedHead)
            throw new InvalidDataException("Trusted memory execution-proof head does not match the external monotonic anchor.");
        if (!validation.EligibleForTrustedPromotion)
            throw new InvalidOperationException("Judge validation is not eligible for Trusted admission.");
        if (!string.Equals(validation.KnowledgeId, candidate.KnowledgeId, StringComparison.Ordinal))
            throw new InvalidDataException("Memory admission validation belongs to different knowledge.");

        var candidateEvidence = candidate.EvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var validatedEvidence = validation.ValidatedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (!candidateEvidence.SequenceEqual(validatedEvidence, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Trusted memory requires independent validation of the exact candidate evidence set.");

        var observations = context.Observations
            .Select(static observation => observation?.Validate()
                ?? throw new InvalidDataException("Memory admission observation cannot be null."))
            .ToArray();
        var byEvidence = observations
            .GroupBy(static observation => observation.EvidenceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (byEvidence.Count != candidateEvidence.Length
            || candidateEvidence.Any(id => !byEvidence.TryGetValue(id, out var matches) || matches.Length != 1)
            || byEvidence.Keys.Any(id => !candidateEvidence.Contains(id, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Trusted memory observations must exactly match candidate evidence one-to-one.");

        var proofs = new List<MemoryAdmissionEvidenceProof>(candidateEvidence.Length);
        foreach (var evidenceId in candidateEvidence)
        {
            var observation = byEvidence[evidenceId][0];
            if (observation.ProjectId != candidate.ProjectId
                || !string.Equals(observation.TargetId, candidate.TargetId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Trusted memory admission cannot cross project or target boundaries.");
            if (observation.ContainsPersonalData
                || observation.Sensitivity == EvidenceSensitivity.PersonalData
                || observation.ContainsRawSecretMaterial)
                throw new InvalidOperationException("Personal data or raw secret material cannot enter Trusted project memory.");

            var executionId = MissionExecutionProofIdentity.CreateExecutionId(
                candidate.ProjectId, context.MissionId, candidate.TargetId,
                observation.SourceTaskId, observation.Specialist);
            var completed = context.ProofRecords.Where(record =>
                    string.Equals(record.Event.ExecutionId, executionId, StringComparison.Ordinal)
                    && record.Event.Stage == ExecutionProofStage.Completed)
                .ToArray();
            if (completed.Length != 1)
                throw new InvalidDataException("Trusted memory evidence must resolve to exactly one completed governed execution.");

            var proof = completed[0];
            if (proof.Event.ProjectId != candidate.ProjectId
                || !string.Equals(proof.Event.RunId, context.MissionId, StringComparison.Ordinal)
                || !string.Equals(proof.Event.CapabilityClass, "mission-specialist", StringComparison.Ordinal)
                || !string.Equals(proof.Event.CapabilityId, observation.Specialist.ToString(), StringComparison.Ordinal)
                || proof.Event.Outcome != ExecutionProofOutcome.Succeeded
                || proof.Event.ResultDigestSha256 is null)
                throw new InvalidDataException("Trusted memory evidence is not backed by a successful governed specialist execution.");

            proofs.Add(new MemoryAdmissionEvidenceProof(
                evidenceId,
                observation.SourceTaskId,
                observation.Specialist,
                executionId,
                proof.RecordHashSha256,
                proof.Event.ResultDigestSha256));
        }

        var digest = ComputeDigest(candidate, validation, context, proofs);
        return new TrustedKnowledgeAdmissionAuthorization(
            candidate.KnowledgeId,
            validation.ValidationRecordId,
            candidate.ProjectId,
            candidate.TargetId,
            context.MissionId,
            context.ExpectedHead,
            digest,
            admittedAt).Validate();
    }

    private static string ComputeDigest(
        CandidateKnowledge candidate,
        KnowledgeValidationRecord validation,
        MemoryAdmissionContext context,
        IReadOnlyList<MemoryAdmissionEvidenceProof> proofs)
    {
        var fields = new List<string>
        {
            "aevrix-project-knowledge-admission-v2",
            candidate.KnowledgeId,
            validation.ValidationRecordId,
            candidate.ProjectId.ToString("D"),
            candidate.TargetId,
            context.MissionId,
            context.ExpectedHead.EntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            context.ExpectedHead.HeadHashSha256.ToLowerInvariant()
        };
        foreach (var proof in proofs.OrderBy(static proof => proof.EvidenceId, StringComparer.Ordinal))
        {
            fields.Add(proof.EvidenceId);
            fields.Add(proof.SourceTaskId);
            fields.Add(proof.Specialist.ToString());
            fields.Add(proof.ExecutionId);
            fields.Add(proof.CompletedRecordHashSha256.ToLowerInvariant());
            fields.Add(proof.ResultDigestSha256.ToLowerInvariant());
        }
        var canonical = string.Concat(fields.Select(value => $"{Encoding.UTF8.GetByteCount(value)}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
