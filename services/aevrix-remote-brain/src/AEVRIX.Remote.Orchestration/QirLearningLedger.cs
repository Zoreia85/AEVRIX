using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public enum QirLearningSensitivity { Public, ProjectConfidential, PersonalData }
public enum QirLearningBasis { Observed, ExperimentallyValidated, Inferred, VendorClaim }

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
        ValidateId(ObservationId, 3, 160);
        ValidateId(PatternKey, 3, 160);
        if (ProjectId == Guid.Empty) throw new InvalidDataException("QIR project id cannot be empty.");
        if (FeatureHash.Length != 64 || FeatureHash.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("QIR feature hash must be SHA-256 hexadecimal.");
        if (!double.IsFinite(Confidence) || Confidence is < 0 or > 1)
            throw new InvalidDataException("QIR confidence is outside [0,1].");
        if (EvidenceIds is null || EvidenceIds.Count is < 1 or > 512
            || EvidenceIds.Any(id => !IsSafeId(id, 3, 160))
            || EvidenceIds.Distinct(StringComparer.Ordinal).Count() != EvidenceIds.Count)
            throw new InvalidDataException("QIR evidence ids are invalid.");
        if (ObservedAt == default) throw new InvalidDataException("QIR timestamp is missing.");
        if (ContainsRawSecretMaterial)
            throw new InvalidDataException("Secrets cannot enter QIR learning.");
        if (ContainsPersonalData && Sensitivity == QirLearningSensitivity.Public)
            throw new InvalidDataException("Personal data cannot be public QIR learning material.");
        return this;
    }

    internal static bool IsSafeId(string value, int min, int max) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= min && value.Length <= max
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');

    internal static void ValidateId(string value, int min, int max)
    {
        if (!IsSafeId(value, min, max)) throw new InvalidDataException("QIR identifier is invalid.");
    }
}

public sealed record QirLearningPolicy(
    int MinimumIndependentProjects = 2,
    double MinimumProjectConfidence = 0.85,
    int MaximumProjectObservationsPerPattern = 64)
{
    public QirLearningPolicy Validate()
    {
        if (MinimumIndependentProjects is < 2 or > 32) throw new ArgumentOutOfRangeException(nameof(MinimumIndependentProjects));
        if (!double.IsFinite(MinimumProjectConfidence) || MinimumProjectConfidence is < 0.5 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumProjectConfidence));
        if (MaximumProjectObservationsPerPattern is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumProjectObservationsPerPattern));
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
    DateTimeOffset PromotedAt);

public sealed class QirLearningLedger
{
    private readonly ConcurrentDictionary<string, QirLearningObservation> _items = new(StringComparer.Ordinal);
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
        var key = $"{observation.ProjectId:D}:{observation.ObservationId}";
        if (_items.TryGetValue(key, out var existing))
        {
            if (Fingerprint(existing) == Fingerprint(observation)) return existing;
            throw new InvalidOperationException("QIR observation id is immutable.");
        }
        if (_items.TryAdd(key, observation)) return observation;
        return Record(observation);
    }

    public IReadOnlyList<QirLearningObservation> ProjectSnapshot(Guid projectId)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        return _items.Values.Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.ObservedAt).ThenBy(x => x.ObservationId, StringComparer.Ordinal).ToArray();
    }

    public QirGlobalPattern Promote(string patternKey, string featureHash)
    {
        QirLearningObservation.ValidateId(patternKey, 3, 160);
        if (featureHash.Length != 64 || featureHash.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("QIR feature hash must be SHA-256 hexadecimal.");

        var projects = _items.Values
            .Where(x => x.EligibleForGlobalLearning
                && string.Equals(x.PatternKey, patternKey, StringComparison.Ordinal)
                && string.Equals(x.FeatureHash, featureHash, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.ProjectId)
            .Select(g => g.OrderByDescending(x => x.Confidence).ThenByDescending(x => x.ObservedAt)
                .Take(_policy.MaximumProjectObservationsPerPattern).ToArray())
            .Where(g => g.Length > 0 && g.Average(x => x.Confidence) >= _policy.MinimumProjectConfidence)
            .ToArray();

        if (projects.Length < _policy.MinimumIndependentProjects)
            throw new InvalidOperationException("QIR global promotion requires multiple independent eligible projects.");

        var confidence = projects.Average(g => g.Average(x => x.Confidence));
        var idMaterial = Encoding.UTF8.GetBytes($"{patternKey}\n{featureHash.ToLowerInvariant()}");
        var id = "QIR-" + Convert.ToHexString(SHA256.HashData(idMaterial).AsSpan(0, 16)).ToLowerInvariant();
        return new QirGlobalPattern(id, patternKey, featureHash.ToLowerInvariant(), projects.Length,
            projects.Sum(g => g.Length), confidence, _time.GetUtcNow());
    }

    private static string Fingerprint(QirLearningObservation x)
    {
        var canonical = string.Join("\n", x.ProjectId.ToString("D"), x.ObservationId, x.PatternKey,
            x.FeatureHash.ToLowerInvariant(), x.Basis, x.Sensitivity,
            x.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string.Join("|", x.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)),
            x.ObservedAt.ToUniversalTime().ToString("O"), x.ContainsPersonalData, x.ContainsRawSecretMaterial);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
