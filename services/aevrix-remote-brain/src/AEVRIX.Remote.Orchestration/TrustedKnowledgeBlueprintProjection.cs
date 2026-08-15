using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public enum BlueprintKnowledgePromotionLevel
{
    Conditional,
    Reconstructable
}

public sealed record BlueprintKnowledgeRequirement(
    string RequirementId,
    Guid ProjectId,
    string TargetId,
    string ClaimKey,
    string Statement,
    EvidenceObservationClass Basis,
    EvidenceSensitivity Sensitivity,
    BlueprintKnowledgePromotionLevel PromotionLevel,
    double Confidence,
    IReadOnlyList<string> EvidenceIds,
    string SourceKnowledgeId,
    string ValidationRecordId)
{
    public BlueprintKnowledgeRequirement Validate()
    {
        if (!MissionTaskSpec.IsSafeId(RequirementId, 3, 160)
            || ProjectId == Guid.Empty
            || !MissionTaskSpec.IsSafeId(TargetId, 2, 128)
            || !MissionTaskSpec.IsSafeId(ClaimKey, 3, 160)
            || !MissionTaskSpec.IsSafeId(SourceKnowledgeId, 3, 160)
            || !MissionTaskSpec.IsSafeId(ValidationRecordId, 3, 160))
        {
            throw new InvalidDataException("Blueprint knowledge requirement identity is invalid.");
        }

        if (string.IsNullOrWhiteSpace(Statement) || Statement.Length > 64_000)
        {
            throw new InvalidDataException("Blueprint knowledge requirement statement is invalid.");
        }

        if (!double.IsFinite(Confidence) || Confidence is < 0 or > 1)
        {
            throw new InvalidDataException("Blueprint knowledge requirement confidence is outside [0,1].");
        }

        if (EvidenceIds is null || EvidenceIds.Count is < 1 or > 2_000
            || EvidenceIds.Any(id => !MissionTaskSpec.IsSafeId(id, 3, 160)))
        {
            throw new InvalidDataException("Blueprint knowledge requirement evidence is invalid.");
        }

        return this;
    }
}

public sealed class TrustedKnowledgeBlueprintProjector
{
    private readonly EvidenceBus _bus;
    private readonly ICandidateKnowledgeRepository _knowledgeRepository;
    private readonly EvidenceFusionEngine _fusion;

    public TrustedKnowledgeBlueprintProjector(
        EvidenceBus bus,
        ICandidateKnowledgeRepository knowledgeRepository,
        EvidenceFusionEngine? fusion = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _knowledgeRepository = knowledgeRepository ?? throw new ArgumentNullException(nameof(knowledgeRepository));
        _fusion = fusion ?? new EvidenceFusionEngine();
    }

    public async Task<BlueprintKnowledgeRequirement> ProjectAsync(
        MissionKnowledgeItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var supplied = item.Knowledge ?? throw new InvalidDataException("Mission knowledge item has no knowledge payload.");
        if (!MissionTaskSpec.IsSafeId(supplied.KnowledgeId, 3, 160))
        {
            throw new InvalidDataException("Blueprint promotion supplied knowledge id is invalid.");
        }

        var knowledge = await _knowledgeRepository.LoadAsync(supplied.KnowledgeId, cancellationToken)
            ?? throw new KeyNotFoundException("Blueprint promotion knowledge was not found in the authoritative repository.");
        ValidateInput(item, knowledge);
        EnsureSuppliedIdentityMatches(supplied, knowledge);

        var promotionLevel = knowledge.TrustState switch
        {
            KnowledgeTrustState.Trusted => BlueprintKnowledgePromotionLevel.Reconstructable,
            KnowledgeTrustState.Validated => BlueprintKnowledgePromotionLevel.Conditional,
            _ => throw new InvalidOperationException("Candidate or rejected knowledge cannot enter blueprint promotion.")
        };

        if (string.IsNullOrWhiteSpace(knowledge.ValidationRecordId))
        {
            throw new InvalidDataException("Blueprint promotion requires an explicit validation record.");
        }

        var observations = knowledge.EvidenceIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => _bus.Load(knowledge.ProjectId, id)
                ?? throw new InvalidDataException($"Blueprint promotion evidence '{id}' is missing from the project Evidence Bus."))
            .ToArray();

        if (observations.Any(observation => observation.ProjectId != knowledge.ProjectId
            || !string.Equals(observation.TargetId, knowledge.TargetId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(observation.ClaimKey, item.ClaimKey, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Blueprint promotion evidence does not match the governed project, target or claim.");
        }

        if (observations.Any(observation => observation.ContainsPersonalData
            || observation.Sensitivity == EvidenceSensitivity.PersonalData))
        {
            throw new InvalidOperationException("Personal data must be sanitized into a non-PII observation before Blueprint promotion.");
        }

        var recalculatedFusion = _fusion.Fuse(knowledge.ProjectId, knowledge.TargetId, item.ClaimKey, observations);
        if (item.FusionState != recalculatedFusion.State)
        {
            throw new InvalidDataException("Blueprint promotion fusion state does not match the independently recalculated evidence state.");
        }
        if (recalculatedFusion.State != EvidenceFusionState.Convergent)
        {
            throw new InvalidOperationException("Only independently convergent evidence may enter blueprint promotion.");
        }

        var basis = ConservativeBasis(observations);
        var sensitivity = observations.MaxBy(observation => (int)observation.Sensitivity)!.Sensitivity;
        var evidenceIds = observations.Select(observation => observation.EvidenceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var requirement = new BlueprintKnowledgeRequirement(
            RequirementId: BuildRequirementId(knowledge.ProjectId, knowledge.TargetId, item.ClaimKey, knowledge.KnowledgeId),
            ProjectId: knowledge.ProjectId,
            TargetId: knowledge.TargetId,
            ClaimKey: item.ClaimKey,
            Statement: knowledge.Statement.Trim(),
            Basis: basis,
            Sensitivity: sensitivity,
            PromotionLevel: promotionLevel,
            Confidence: Math.Min(knowledge.Confidence, recalculatedFusion.Confidence),
            EvidenceIds: evidenceIds,
            SourceKnowledgeId: knowledge.KnowledgeId,
            ValidationRecordId: knowledge.ValidationRecordId);

        return requirement.Validate();
    }

    private static void ValidateInput(MissionKnowledgeItem item, CandidateKnowledge knowledge)
    {
        if (!MissionTaskSpec.IsSafeId(item.ClaimKey, 3, 160)
            || knowledge.ProjectId == Guid.Empty
            || !MissionTaskSpec.IsSafeId(knowledge.TargetId, 2, 128)
            || !MissionTaskSpec.IsSafeId(knowledge.KnowledgeId, 3, 160))
        {
            throw new InvalidDataException("Blueprint promotion knowledge identity is invalid.");
        }
        if (string.IsNullOrWhiteSpace(knowledge.Statement) || knowledge.Statement.Length > 64_000)
        {
            throw new InvalidDataException("Blueprint promotion knowledge statement is invalid.");
        }
        if (!double.IsFinite(knowledge.Confidence) || knowledge.Confidence is < 0 or > 1)
        {
            throw new InvalidDataException("Blueprint promotion knowledge confidence is outside [0,1].");
        }
        if (knowledge.EvidenceIds is null || knowledge.EvidenceIds.Count is < 1 or > 2_000
            || knowledge.EvidenceIds.Any(id => !MissionTaskSpec.IsSafeId(id, 3, 160)))
        {
            throw new InvalidDataException("Blueprint promotion knowledge evidence set is invalid.");
        }
    }

    private static void EnsureSuppliedIdentityMatches(CandidateKnowledge supplied, CandidateKnowledge authoritative)
    {
        if (!string.Equals(supplied.KnowledgeId, authoritative.KnowledgeId, StringComparison.Ordinal)
            || supplied.ProjectId != authoritative.ProjectId
            || !string.Equals(supplied.TargetId, authoritative.TargetId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Supplied mission knowledge identity does not match the authoritative repository record.");
        }
    }

    private static EvidenceObservationClass ConservativeBasis(IReadOnlyCollection<EvidenceObservation> observations)
    {
        if (observations.Any(item => item.ObservationClass == EvidenceObservationClass.VendorClaim))
        {
            return EvidenceObservationClass.VendorClaim;
        }
        if (observations.Any(item => item.ObservationClass == EvidenceObservationClass.Inferred))
        {
            return EvidenceObservationClass.Inferred;
        }
        if (observations.Any(item => item.ObservationClass == EvidenceObservationClass.Observed))
        {
            return EvidenceObservationClass.Observed;
        }
        return EvidenceObservationClass.ExperimentallyValidated;
    }

    private static string BuildRequirementId(Guid projectId, string targetId, string claimKey, string knowledgeId)
    {
        var canonical = $"{projectId:D}\n{targetId}\n{claimKey}\n{knowledgeId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "BKR-" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
