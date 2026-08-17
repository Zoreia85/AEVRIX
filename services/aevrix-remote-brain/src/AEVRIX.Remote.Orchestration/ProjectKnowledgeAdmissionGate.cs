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
        {
            throw new InvalidDataException("Memory admission mission id is invalid.");
        }

        ArgumentNullException.ThrowIfNull(Observations);
        ArgumentNullException.ThrowIfNull(ProofRecords);
        ArgumentNullException.ThrowIfNull(ExpectedHead);
        if (Observations.Count is < 1 or > 2_000)
        {
            throw new InvalidDataException("Memory admission observation count is invalid.");
        }

        ExecutionProofLedger.VerifySnapshot(ProofRecords, ExpectedHead);
        return this;
    }
}

public sealed record MemoryAdmissionEvidenceProof(
    string EvidenceId,
    string SourceTaskId,
    MissionSpecialistKind Specialist,
    string ExecutionId,
    string CompletedRecordHashSha256,
    string ResultDigestSha256);

public sealed record MemoryAdmissionReceipt(
    string KnowledgeId,
    string ValidationRecordId,
    Guid ProjectId,
    string TargetId,
    string MissionId,
    ExecutionProofHead LedgerHead,
    IReadOnlyList<MemoryAdmissionEvidenceProof> EvidenceProofs,
    string AdmissionDigestSha256,
    DateTimeOffset AdmittedAt);

public interface IMemoryAdmissionGate
{
    Task<MemoryAdmissionReceipt> AdmitTrustedAsync(
        CandidateKnowledge candidate,
        KnowledgeValidationRecord validation,
        MemoryAdmissionContext context,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default);
}

internal interface ITrustedKnowledgePromotionStore
{
    Task PromoteTrustedAsync(
        string knowledgeId,
        string validationRecordId,
        DateTimeOffset promotedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only authority permitted to turn validated project knowledge into Trusted memory.
/// It closes every admitted EvidenceId against the exact verified ExecutionProofLedger snapshot
/// and persists no raw evidence in the admission receipt.
/// </summary>
public sealed class ProjectKnowledgeAdmissionGate : IMemoryAdmissionGate
{
    private readonly ICandidateKnowledgeRepository _repository;
    private readonly ITrustedKnowledgePromotionStore _promotionStore;

    public ProjectKnowledgeAdmissionGate(ICandidateKnowledgeRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _promotionStore = repository as ITrustedKnowledgePromotionStore
            ?? throw new ArgumentException(
                "Trusted memory admission requires a repository with the internal trusted-promotion boundary.",
                nameof(repository));
    }

    public async Task<MemoryAdmissionReceipt> AdmitTrustedAsync(
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

        if (admittedAt == default)
        {
            throw new ArgumentOutOfRangeException(nameof(admittedAt));
        }
        if (candidate.TrustState != KnowledgeTrustState.Candidate)
        {
            throw new InvalidOperationException("Only Candidate knowledge may enter trusted memory admission.");
        }

        validation.Validate();
        context.Validate();
        if (!validation.EligibleForTrustedPromotion)
        {
            throw new InvalidOperationException("Judge validation is not eligible for Trusted admission.");
        }
        if (!string.Equals(validation.KnowledgeId, candidate.KnowledgeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Memory admission validation belongs to different knowledge.");
        }

        var candidateEvidence = candidate.EvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var validatedEvidence = validation.ValidatedEvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (!candidateEvidence.SequenceEqual(validatedEvidence, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Trusted memory requires independent validation of the exact candidate evidence set.");
        }

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
        {
            throw new InvalidDataException("Trusted memory observations must exactly match candidate evidence one-to-one.");
        }

        var proofs = new List<MemoryAdmissionEvidenceProof>(candidateEvidence.Length);
        foreach (var evidenceId in candidateEvidence)
        {
            var observation = byEvidence[evidenceId][0];
            if (observation.ProjectId != candidate.ProjectId
                || !string.Equals(observation.TargetId, candidate.TargetId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Trusted memory admission cannot cross project or target boundaries.");
            }
            if (observation.ContainsPersonalData
                || observation.Sensitivity == EvidenceSensitivity.PersonalData
                || observation.ContainsRawSecretMaterial)
            {
                throw new InvalidOperationException("Personal data or raw secret material cannot enter Trusted project memory.");
            }

            var executionId = MissionExecutionProofIdentity.CreateExecutionId(
                candidate.ProjectId,
                context.MissionId,
                candidate.TargetId,
                observation.SourceTaskId,
                observation.Specialist);
            var completed = context.ProofRecords.Where(record =>
                    string.Equals(record.Event.ExecutionId, executionId, StringComparison.Ordinal)
                    && record.Event.Stage == ExecutionProofStage.Completed)
                .ToArray();
            if (completed.Length != 1)
            {
                throw new InvalidDataException("Trusted memory evidence must resolve to exactly one completed governed execution.");
            }

            var proof = completed[0];
            if (proof.Event.ProjectId != candidate.ProjectId
                || !string.Equals(proof.Event.RunId, context.MissionId, StringComparison.Ordinal)
                || !string.Equals(proof.Event.CapabilityClass, "mission-specialist", StringComparison.Ordinal)
                || !string.Equals(proof.Event.CapabilityId, observation.Specialist.ToString(), StringComparison.Ordinal)
                || proof.Event.Outcome != ExecutionProofOutcome.Succeeded
                || proof.Event.ResultDigestSha256 is null)
            {
                throw new InvalidDataException("Trusted memory evidence is not backed by a successful governed specialist execution.");
            }

            proofs.Add(new MemoryAdmissionEvidenceProof(
                evidenceId,
                observation.SourceTaskId,
                observation.Specialist,
                executionId,
                proof.RecordHashSha256,
                proof.Event.ResultDigestSha256));
        }

        var digest = ComputeDigest(candidate, validation, context, proofs);
        await _promotionStore.PromoteTrustedAsync(
            candidate.KnowledgeId,
            validation.ValidationRecordId,
            admittedAt,
            cancellationToken).ConfigureAwait(false);

        var promoted = await _repository.LoadAsync(candidate.KnowledgeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Trusted memory promotion disappeared from the authoritative repository.");
        if (promoted.TrustState != KnowledgeTrustState.Trusted
            || !string.Equals(promoted.ValidationRecordId, validation.ValidationRecordId, StringComparison.Ordinal)
            || promoted.ProjectId != candidate.ProjectId
            || !string.Equals(promoted.TargetId, candidate.TargetId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Trusted memory repository state does not match the admission decision.");
        }

        return new MemoryAdmissionReceipt(
            candidate.KnowledgeId,
            validation.ValidationRecordId,
            candidate.ProjectId,
            candidate.TargetId,
            context.MissionId,
            context.ExpectedHead,
            proofs.OrderBy(static proof => proof.EvidenceId, StringComparer.Ordinal).ToArray(),
            digest,
            admittedAt);
    }

    internal static string ComputeDigest(
        CandidateKnowledge candidate,
        KnowledgeValidationRecord validation,
        MemoryAdmissionContext context,
        IReadOnlyList<MemoryAdmissionEvidenceProof> proofs)
    {
        var fields = new List<string>
        {
            "aevrix-project-knowledge-admission-v1",
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
