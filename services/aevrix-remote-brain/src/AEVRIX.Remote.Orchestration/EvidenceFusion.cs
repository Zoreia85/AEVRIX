using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public enum EvidenceObservationClass
{
    Observed,
    ExperimentallyValidated,
    Inferred,
    VendorClaim
}

public enum EvidenceSensitivity
{
    Public,
    ProjectConfidential,
    PersonalData
}

public enum EvidenceFusionState
{
    Insufficient,
    Convergent,
    Contested
}

public sealed record EvidenceObservation(
    string EvidenceId,
    Guid ProjectId,
    string TargetId,
    string SourceTaskId,
    MissionSpecialistKind Specialist,
    EvidenceObservationClass ObservationClass,
    EvidenceSensitivity Sensitivity,
    string ClaimKey,
    string ClaimValue,
    string Summary,
    double Confidence,
    string ContentSha256,
    DateTimeOffset ObservedAt,
    IReadOnlyList<string> SourceArtifactIds,
    IReadOnlyList<string> ParentEvidenceIds,
    bool ContainsPersonalData = false,
    bool ContainsRawSecretMaterial = false)
{
    public bool EligibleForGlobalLearning =>
        Sensitivity == EvidenceSensitivity.Public
        && !ContainsPersonalData
        && !ContainsRawSecretMaterial
        && ObservationClass is EvidenceObservationClass.Observed or EvidenceObservationClass.ExperimentallyValidated;

    public EvidenceObservation Validate()
    {
        if (!MissionTaskSpec.IsSafeId(EvidenceId, 3, 160))
        {
            throw new InvalidDataException("Evidence observation id is invalid.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new InvalidDataException("Evidence observation project id cannot be empty.");
        }

        if (!MissionTaskSpec.IsSafeId(TargetId, 2, 128)
            || !MissionTaskSpec.IsSafeId(SourceTaskId, 3, 128)
            || !MissionTaskSpec.IsSafeId(ClaimKey, 3, 160))
        {
            throw new InvalidDataException("Evidence observation target, source task or claim key is invalid.");
        }

        if (string.IsNullOrWhiteSpace(ClaimValue) || ClaimValue.Length > 4_096)
        {
            throw new InvalidDataException("Evidence claim value is missing or too large.");
        }

        if (string.IsNullOrWhiteSpace(Summary) || Summary.Length > 16_000)
        {
            throw new InvalidDataException("Evidence observation summary is missing or too large.");
        }

        if (!double.IsFinite(Confidence) || Confidence is < 0 or > 1)
        {
            throw new InvalidDataException("Evidence observation confidence is outside [0,1].");
        }

        if (string.IsNullOrWhiteSpace(ContentSha256)
            || ContentSha256.Length != 64
            || !ContentSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Evidence observation content hash must be SHA-256 hexadecimal.");
        }

        if (ObservedAt == default)
        {
            throw new InvalidDataException("Evidence observation timestamp is missing.");
        }

        if (SourceArtifactIds is null
            || ParentEvidenceIds is null
            || SourceArtifactIds.Count > 512
            || SourceArtifactIds.Any(id => !MissionTaskSpec.IsSafeId(id, 3, 160))
            || ParentEvidenceIds.Count is < 1 or > 512
            || ParentEvidenceIds.Any(id => !MissionTaskSpec.IsSafeId(id, 3, 160)))
        {
            throw new InvalidDataException("Evidence observation provenance exceeds safe bounds.");
        }

        if (ParentEvidenceIds.Contains(EvidenceId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Evidence observation cannot cite itself as a parent.");
        }

        if (ContainsRawSecretMaterial)
        {
            throw new InvalidDataException("Raw credentials, tokens, private keys or session secrets cannot enter the Evidence Bus.");
        }

        if (ContainsPersonalData && Sensitivity == EvidenceSensitivity.Public)
        {
            throw new InvalidDataException("Evidence containing personal data cannot be classified as public.");
        }

        return this;
    }
}

public sealed class EvidenceBus
{
    private readonly ConcurrentDictionary<string, EvidenceObservation> _observations =
        new(StringComparer.OrdinalIgnoreCase);

    public EvidenceObservation PublishFromSpecialist(
        SpecialistExecutionContext context,
        EvidenceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observation);
        observation.Validate();
        context.Task.Validate();

        if (observation.ProjectId != context.ProjectId
            || !string.Equals(observation.TargetId, context.TargetId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(observation.SourceTaskId, context.Task.TaskId, StringComparison.OrdinalIgnoreCase)
            || observation.Specialist != context.Task.Specialist)
        {
            throw new InvalidDataException("Evidence observation provenance does not match the specialist execution context.");
        }

        var allowedParents = context.Task.EvidenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (observation.ParentEvidenceIds.Any(id => !allowedParents.Contains(id)))
        {
            throw new InvalidDataException("Evidence observation cites parent evidence outside the governed task boundary.");
        }

        var key = BuildKey(observation.ProjectId, observation.EvidenceId);
        while (true)
        {
            if (_observations.TryGetValue(key, out var existing))
            {
                if (string.Equals(Fingerprint(existing), Fingerprint(observation), StringComparison.Ordinal))
                {
                    return existing;
                }

                throw new InvalidOperationException("Evidence observation id is immutable and cannot be rebound to different content.");
            }

            if (_observations.TryAdd(key, observation))
            {
                return observation;
            }
        }
    }

    public EvidenceObservation? Load(Guid projectId, string evidenceId)
    {
        if (projectId == Guid.Empty || !MissionTaskSpec.IsSafeId(evidenceId, 3, 160))
        {
            return null;
        }

        return _observations.TryGetValue(BuildKey(projectId, evidenceId), out var observation)
            ? observation
            : null;
    }

    public IReadOnlyList<EvidenceObservation> Snapshot(Guid projectId, string targetId)
    {
        if (projectId == Guid.Empty || !MissionTaskSpec.IsSafeId(targetId, 2, 128))
        {
            throw new ArgumentException("Evidence snapshot scope is invalid.");
        }

        return _observations.Values
            .Where(item => item.ProjectId == projectId
                && string.Equals(item.TargetId, targetId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.ObservedAt)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<EvidenceObservation> GlobalLearningEligibleSnapshot(Guid projectId, string targetId) =>
        Snapshot(projectId, targetId)
            .Where(item => item.EligibleForGlobalLearning)
            .ToArray();

    private static string BuildKey(Guid projectId, string evidenceId) =>
        $"{projectId:D}:{evidenceId}";

    private static string Fingerprint(EvidenceObservation observation)
    {
        var canonical = string.Join("\n", new[]
        {
            observation.ProjectId.ToString("D"),
            observation.TargetId,
            observation.EvidenceId,
            observation.SourceTaskId,
            observation.Specialist.ToString(),
            observation.ObservationClass.ToString(),
            observation.Sensitivity.ToString(),
            observation.ClaimKey,
            observation.ClaimValue.Trim(),
            observation.Summary.Trim(),
            observation.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            observation.ContentSha256.ToLowerInvariant(),
            observation.ObservedAt.ToUniversalTime().ToString("O"),
            string.Join("|", observation.SourceArtifactIds.OrderBy(x => x, StringComparer.Ordinal)),
            string.Join("|", observation.ParentEvidenceIds.OrderBy(x => x, StringComparer.Ordinal)),
            observation.ContainsPersonalData.ToString(),
            observation.ContainsRawSecretMaterial.ToString()
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record EvidenceFusionPolicy(
    int MinimumIndependentSources = 2,
    int MinimumIndependentSpecialists = 2,
    double ContestedConfidencePenalty = 0.25)
{
    public EvidenceFusionPolicy Validate()
    {
        if (MinimumIndependentSources is < 1 or > 16
            || MinimumIndependentSpecialists is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumIndependentSources));
        }

        if (!double.IsFinite(ContestedConfidencePenalty)
            || ContestedConfidencePenalty is < 0 or > 0.9)
        {
            throw new ArgumentOutOfRangeException(nameof(ContestedConfidencePenalty));
        }

        return this;
    }
}

public sealed record EvidenceClaimAlternative(
    string RepresentativeValue,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<MissionSpecialistKind> Specialists,
    int IndependentSourceCount,
    double Confidence);

public sealed record EvidenceFusionCandidate(
    Guid ProjectId,
    string TargetId,
    string ClaimKey,
    EvidenceFusionState State,
    string? PreferredValue,
    double Confidence,
    IReadOnlyList<EvidenceClaimAlternative> Alternatives,
    IReadOnlyList<string> EvidenceIds,
    bool EligibleForGlobalLearning)
{
    public bool RequiresJudgeValidation => true;
    public bool HasConflict => State == EvidenceFusionState.Contested;
}

public sealed class EvidenceFusionEngine
{
    private readonly EvidenceFusionPolicy _policy;

    public EvidenceFusionEngine(EvidenceFusionPolicy? policy = null)
    {
        _policy = (policy ?? new EvidenceFusionPolicy()).Validate();
    }

    public EvidenceFusionCandidate Fuse(
        Guid projectId,
        string targetId,
        string claimKey,
        IReadOnlyCollection<EvidenceObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        if (projectId == Guid.Empty
            || !MissionTaskSpec.IsSafeId(targetId, 2, 128)
            || !MissionTaskSpec.IsSafeId(claimKey, 3, 160))
        {
            throw new ArgumentException("Evidence fusion scope is invalid.");
        }

        if (observations.Count == 0)
        {
            throw new ArgumentException("Evidence fusion requires at least one observation.", nameof(observations));
        }

        foreach (var observation in observations)
        {
            if (observation is null)
            {
                throw new InvalidDataException("Evidence fusion cannot contain null observations.");
            }

            observation.Validate();
            if (observation.ProjectId != projectId
                || !string.Equals(observation.TargetId, targetId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(observation.ClaimKey, claimKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Evidence fusion cannot mix projects, targets or claim keys.");
            }
        }

        var grouped = observations
            .GroupBy(item => NormalizeValue(item.ClaimValue), StringComparer.Ordinal)
            .Select(group => BuildAlternative(group.ToArray()))
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.RepresentativeValue, StringComparer.Ordinal)
            .ToArray();

        var top = grouped[0];
        EvidenceFusionState state;
        string? preferredValue = null;
        double confidence;
        bool eligibleForGlobalLearning = false;

        if (grouped.Length > 1)
        {
            state = EvidenceFusionState.Contested;
            confidence = Math.Clamp(top.Confidence * (1 - _policy.ContestedConfidencePenalty), 0, 1);
        }
        else if (top.IndependentSourceCount >= _policy.MinimumIndependentSources
            && top.Specialists.Count >= _policy.MinimumIndependentSpecialists)
        {
            state = EvidenceFusionState.Convergent;
            preferredValue = top.RepresentativeValue;
            confidence = top.Confidence;
            eligibleForGlobalLearning = observations.All(item => item.EligibleForGlobalLearning);
        }
        else
        {
            state = EvidenceFusionState.Insufficient;
            confidence = Math.Clamp(top.Confidence * 0.75, 0, 1);
        }

        return new EvidenceFusionCandidate(
            projectId,
            targetId,
            claimKey,
            state,
            preferredValue,
            confidence,
            grouped,
            observations.Select(item => item.EvidenceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            eligibleForGlobalLearning);
    }

    private EvidenceClaimAlternative BuildAlternative(IReadOnlyCollection<EvidenceObservation> observations)
    {
        var sourceCount = observations
            .Select(item => $"{item.Specialist}:{item.SourceTaskId}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var specialists = observations
            .Select(item => item.Specialist)
            .Distinct()
            .OrderBy(item => item)
            .ToArray();

        var adjustedAverage = observations.Average(item =>
            item.Confidence * ObservationWeight(item.ObservationClass));
        var sourceFactor = Math.Min(1.0, (double)sourceCount / _policy.MinimumIndependentSources);
        var specialistFactor = Math.Min(1.0, (double)specialists.Length / _policy.MinimumIndependentSpecialists);
        var confidence = Math.Clamp(adjustedAverage * Math.Min(sourceFactor, specialistFactor), 0, 1);

        return new EvidenceClaimAlternative(
            RepresentativeValue: observations.OrderByDescending(item => item.Confidence).First().ClaimValue.Trim(),
            EvidenceIds: observations.Select(item => item.EvidenceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            Specialists: specialists,
            IndependentSourceCount: sourceCount,
            Confidence: confidence);
    }

    private static double ObservationWeight(EvidenceObservationClass observationClass) =>
        observationClass switch
        {
            EvidenceObservationClass.ExperimentallyValidated => 1.00,
            EvidenceObservationClass.Observed => 0.95,
            EvidenceObservationClass.Inferred => 0.75,
            EvidenceObservationClass.VendorClaim => 0.60,
            _ => 0.50
        };

    private static string NormalizeValue(string value) =>
        value.Trim().ToUpperInvariant();
}
