using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public enum QirLearningSensitivity
{
    Public,
    ProjectConfidential,
    PersonalData
}

public enum QirLearningBasis
{
    Observed,
    ExperimentallyValidated,
    Inferred,
    VendorClaim
}

public sealed record QirLearningObservation(
    string ObservationId,
    Guid ProjectId,
    string PatternKey,
    string FeatureHash,
    QirLearningBasis Basis,
    QirLearningSensitivity Sensitivity,
    double Confidence,
    IReadOnlyList<string> EvidenceIds,
    DateTimeOffset ObservedAt,
    bool ContainsPersonalData = false,
    bool ContainsRawSecretMaterial = false)
{
    public bool EligibleForGlobalLearning =>
        Sensitivity == QirLearningSensitivity.Public
        && !ContainsPersonalData
        && !ContainsRawSecretMaterial
        && Confidence >= 0.80
        && Basis is QirLearningBasis.Observed or QirLearningBasis.ExperimentallyValidated;

    public QirLearningObservation Validate()
    {
        ValidateId(ObservationId, 3, 160, nameof(ObservationId));
        ValidateId(PatternKey, 3, 160, nameof(PatternKey));
        if (ProjectId == Guid.Empty)
        {
            throw new InvalidDataException("QIR observation project id cannot be empty.");
        }
        if (FeatureHash.Length != 64 || FeatureHash.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException("QIR feature hash must be SHA-256 hexadecimal.");
        }
        if (!double.IsFinite(Confidence) || Confidence is < 0 or > 1)
        {
            throw new InvalidDataException("QIR observation confidence is outside [0,1].");
        }
        if (EvidenceIds is null || EvidenceIds.Count is < 1 or > 512
            || EvidenceIds.Any(id => !IsSafeId(id, 3, 160))
            || EvidenceIds.Distinct(StringComparer.Ordinal).Count() != EvidenceIds.Count)
        {
            throw new InvalidDataException("QIR observation evidence ids are invalid.");
        }
        if (ObservedAt == default)
        {
            throw new InvalidDataException("QIR observation timestamp is missing.");
        }
        if (ContainsRawSecretMaterial)
        {
            throw new InvalidDataException("Raw credentials, tokens, keys and session secrets cannot enter QIR learning.");
        }
        if (ContainsPersonalData && Sensitivity == QirLearningSensitivity.Public)
        {
            throw new InvalidDataException("QIR observations containing personal data cannot be public.");
        }
        return this;
    }

    internal static bool IsSafeId(string value, int min, int max) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= min
        && value.Length <= max
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');

    internal static void ValidateId(string value, int min, int max, string name)
    {
        if (!IsSafeId(value, min, max))
        {
            throw new InvalidDataException($"QIR {name} is invalid.");
        }
    }
}

public sealed record QirLearningPolicy(
    int MinimumIndependentProjects = 2,
    double MinimumProjectConfidence = 0.85,
    int MaximumProjectObservationsPerPattern = 64)
{
    public QirLearningPolicy Validate()
    {
        if (MinimumIndependentProjects is < 2 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumIndependentProjects));
        }
        if (!double.IsFinite(MinimumProjectConfidence) || MinimumProjectConfidence is < 0.5 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumProjectConfidence));
        }
        if (MaximumProjectObservationsPerPattern is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumProjectObservationsPerPattern));
        }
        return this;
    }
}

public sealed record QirGlobalPattern(
    string PatternId,
    string PatternKey,
    string FeatureHash,
    int IndependentProjectCount,
    int ObservationCount,
    double Confidence,
    DateTimeOffset PromotedAt)
{
    // Intentionally contains no ProjectId, evidence id, target id, user data or raw source material.
}

public sealed class QirLearningLedger
{
    private readonly ConcurrentDictionary<string, QirLearningObservation> _observations =
        new(StringComparer.Ordinal);
    private readonly QirLearningPolicy _policy;
    private readonly TimeProvider _time;

    public QirLearningLedger(QirLearningPolicy? policy = null, TimeProvider? timeProvider = null)
    {
        _policy = (policy ?? new QirLearningPolicy()).Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    public QirLearningObservation Record(QirLearningObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.Validate();
        var key = BuildObservationKey(observation.ProjectId, observation.ObservationId);
        while (true)
        {
            if (_observations.TryGetValue(key, out var existing))
            {
                if (string.Equals(Fingerprint(existing), Fingerprint(observation), StringComparison.Ordinal))
                {
                    return existing;
                }
                throw new InvalidOperationException("QIR observation id is immutable and cannot be rebound to different content.");
            }
            if (_observations.TryAdd(key, observation))
            {
                return observation;
            }
        }
    }

    public IReadOnlyList<QirLearningObservation> ProjectSnapshot(Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }
        return _observations.Values
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.ObservedAt)
            .ThenBy(item => item.ObservationId, StringComparer.Ordinal)
            .ToArray();
    }

    public QirGlobalPattern Promote(string patternKey, string featureHash)
    {
        QirLearningObservation.ValidateId(patternKey, 3, 160, nameof(patternKey));
        if (featureHash.Length != 64 || featureHash.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException("QIR feature hash must be SHA-256 hexadecimal.");
        }

        var eligible = _observations.Values
            .Where(item => item.EligibleForGlobalLearning
                && string.Equals(item.PatternKey, patternKey, StringComparison.Ordinal)
                && string.Equals(item.FeatureHash, featureHash, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.ProjectId)
            .Select(group => group
                .OrderByDescending(item => item.Confidence)
                .ThenByDescending(item => item.ObservedAt)
                .Take(_policy.MaximumProjectObservationsPerPattern)
                .ToArray())
            .Where(group => group.Length > 0 && group.Average(item => item.Confidence) >= _policy.MinimumProjectConfidence)
            .ToArray();

        if (eligible.Length < _policy.MinimumIndependentProjects)
        {
            throw new InvalidOperationException("QIR global promotion requires independent support from multiple eligible projects.");
        }

        // Equal project weighting prevents one noisy workspace from dominating the learned pattern.
        var confidence = eligible.Average(group => group.Average(item => item.Confidence));
        var observationCount = eligible.Sum(group => group.Length);
        var patternId = BuildPatternId(patternKey, featureHash);
        return new QirGlobalPattern(
            patternId,
            patternKey,
            featureHash.ToLowerInvariant(),
            eligible.Length,
            observationCount,
            confidence,
            _time.GetUtcNow());
    }

    private static string BuildObservationKey(Guid projectId, string observationId) =>
        $"{projectId:D}:{observationId}";

    private static string BuildPatternId(string patternKey, string featureHash)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{patternKey}\n{featureHash.ToLowerInvariant()}"));
        return "QIR-" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string Fingerprint(QirLearningObservation observation)
    {
        var canonical = string.Join("\n", new[]
        {
            observation.ProjectId.ToString("D"), observation.ObservationId, observation.PatternKey,
            observation.FeatureHash.ToLowerInvariant(), observation.Basis.ToString(), observation.Sensitivity.ToString(),
            observation.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string.Join("|", observation.EvidenceIds.OrderBy(x => x, StringComparer.Ordinal)),
            observation.ObservedAt.ToUniversalTime().ToString("O"),
            observation.ContainsPersonalData.ToString(), observation.ContainsRawSecretMaterial.ToString()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
