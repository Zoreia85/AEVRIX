using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public enum KnowledgeTrustState
{
    Candidate,
    Validated,
    Trusted,
    Rejected
}

public enum ModelRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public sealed record AnalysisTask(
    string TaskId,
    Guid ProjectId,
    string TargetId,
    string Objective,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyDictionary<string, string> Context)
{
    public AnalysisTask Validate()
    {
        if (!IsSafeId(TaskId, 8, 128))
        {
            throw new ArgumentException("Analysis task id is invalid.", nameof(TaskId));
        }
        if (ProjectId == Guid.Empty)
        {
            throw new ArgumentException("Analysis task project id cannot be empty.", nameof(ProjectId));
        }
        if (!IsSafeId(TargetId, 2, 64) || string.IsNullOrWhiteSpace(Objective) || Objective.Length > 16_000)
        {
            throw new ArgumentException("Analysis task target/objective is invalid.");
        }
        if (EvidenceIds.Count == 0 || EvidenceIds.Count > 2_000 || EvidenceIds.Any(id => !IsSafeId(id, 3, 160)))
        {
            throw new ArgumentException("Analysis task requires bounded evidence ids.", nameof(EvidenceIds));
        }
        if (Context.Count > 256 || Context.Any(pair => pair.Key.Length is < 1 or > 120 || pair.Value.Length > 8_000))
        {
            throw new ArgumentException("Analysis task context exceeds safe limits.", nameof(Context));
        }
        return this;
    }

    private static bool IsSafeId(string value, int min, int max) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= min
        && value.Length <= max
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');
}

public sealed record ModelAnalysisCandidate(
    string ProviderId,
    string ProviderModelVersion,
    string Statement,
    double Confidence,
    ModelRiskLevel Risk,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OpenQuestions)
{
    public ModelAnalysisCandidate Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length > 120
            || string.IsNullOrWhiteSpace(ProviderModelVersion) || ProviderModelVersion.Length > 160)
        {
            throw new InvalidDataException("Model candidate provider identity is missing or invalid.");
        }
        if (string.IsNullOrWhiteSpace(Statement) || Statement.Length > 64_000)
        {
            throw new InvalidDataException("Model candidate statement is empty or too large.");
        }
        if (double.IsNaN(Confidence) || Confidence is < 0 or > 1)
        {
            throw new InvalidDataException("Model candidate confidence is outside [0,1].");
        }
        if (EvidenceIds.Count == 0 || EvidenceIds.Count > 2_000)
        {
            throw new InvalidDataException("Model candidate must cite bounded evidence ids.");
        }
        if (Assumptions.Count > 256 || OpenQuestions.Count > 256)
        {
            throw new InvalidDataException("Model candidate assumption/question list is too large.");
        }
        return this;
    }
}

public sealed record CandidateKnowledge(
    string KnowledgeId,
    Guid ProjectId,
    string TargetId,
    string Statement,
    KnowledgeTrustState TrustState,
    double Confidence,
    ModelRiskLevel Risk,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> ProviderTrace,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OpenQuestions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ValidationRecordId = null);

public sealed record KnowledgeValidationRecord(
    string ValidationRecordId,
    string KnowledgeId,
    bool EvidenceIntegrityPassed,
    bool EvidenceSupportsStatement,
    bool IndependentValidationPassed,
    bool CounterexampleReviewPassed,
    IReadOnlyList<string> ValidatedEvidenceIds,
    IReadOnlyList<string> Counterexamples,
    DateTimeOffset ValidatedAt)
{
    public bool EligibleForTrustedPromotion =>
        EvidenceIntegrityPassed
        && EvidenceSupportsStatement
        && IndependentValidationPassed
        && CounterexampleReviewPassed
        && ValidatedEvidenceIds.Count > 0;

    public KnowledgeValidationRecord Validate()
    {
        if (string.IsNullOrWhiteSpace(ValidationRecordId) || ValidationRecordId.Length > 160
            || string.IsNullOrWhiteSpace(KnowledgeId) || KnowledgeId.Length > 160)
        {
            throw new InvalidDataException("Knowledge validation identity is invalid.");
        }
        if (ValidatedEvidenceIds.Count > 2_000 || Counterexamples.Count > 512)
        {
            throw new InvalidDataException("Knowledge validation evidence/counterexample list exceeds its limit.");
        }
        return this;
    }
}

public sealed record JudgePolicy(
    double PrimaryAcceptConfidence = 0.92,
    double EscalationConfidence = 0.78,
    ModelRiskLevel MaximumSingleProviderRisk = ModelRiskLevel.Low)
{
    public JudgePolicy Validate()
    {
        if (PrimaryAcceptConfidence is < 0.5 or > 1
            || EscalationConfidence is < 0 or > PrimaryAcceptConfidence)
        {
            throw new ArgumentOutOfRangeException(nameof(PrimaryAcceptConfidence));
        }
        return this;
    }
}

public interface IAevrixModelProvider
{
    string ProviderId { get; }
    Task<ModelAnalysisCandidate> AnalyzeAsync(AnalysisTask task, CancellationToken cancellationToken = default);
}

public interface ICandidateKnowledgeRepository
{
    Task StoreCandidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default);
    Task<CandidateKnowledge?> LoadAsync(string knowledgeId, CancellationToken cancellationToken = default);
    Task StoreValidationAsync(KnowledgeValidationRecord validation, CancellationToken cancellationToken = default);
    Task PromoteAsync(string knowledgeId, KnowledgeTrustState state, string validationRecordId, DateTimeOffset promotedAt, CancellationToken cancellationToken = default);
}

public interface IEvidenceValidationService
{
    Task<KnowledgeValidationRecord> ValidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default);
}

public sealed class OrchestratorJudge
{
    private readonly IAevrixModelProvider _primary;
    private readonly IAevrixModelProvider? _secondary;
    private readonly ICandidateKnowledgeRepository _repository;
    private readonly IEvidenceValidationService _validator;
    private readonly JudgePolicy _policy;
    private readonly TimeProvider _time;

    public OrchestratorJudge(
        IAevrixModelProvider primary,
        ICandidateKnowledgeRepository repository,
        IEvidenceValidationService validator,
        IAevrixModelProvider? secondary = null,
        JudgePolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _secondary = secondary;
        _policy = (policy ?? new JudgePolicy()).Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Produces candidate knowledge only. No model output can become trusted memory in this method.
    /// </summary>
    public async Task<CandidateKnowledge> AnalyzeToCandidateAsync(
        AnalysisTask task,
        CancellationToken cancellationToken = default)
    {
        task.Validate();
        var primary = (await _primary.AnalyzeAsync(task, cancellationToken)).Validate();
        EnsureEvidenceSubset(task, primary);

        ModelAnalysisCandidate selected = primary;
        var trace = new List<string> { Trace(primary) };
        var mustEscalate = primary.Confidence < _policy.PrimaryAcceptConfidence
            || primary.Risk > _policy.MaximumSingleProviderRisk;

        if (mustEscalate && _secondary is not null)
        {
            var secondary = (await _secondary.AnalyzeAsync(task, cancellationToken)).Validate();
            EnsureEvidenceSubset(task, secondary);
            trace.Add(Trace(secondary));
            selected = Consolidate(primary, secondary);
        }

        var now = _time.GetUtcNow();
        var candidate = new CandidateKnowledge(
            KnowledgeId: BuildKnowledgeId(task, selected),
            ProjectId: task.ProjectId,
            TargetId: task.TargetId,
            Statement: selected.Statement.Trim(),
            TrustState: KnowledgeTrustState.Candidate,
            Confidence: selected.Confidence,
            Risk: selected.Risk,
            EvidenceIds: selected.EvidenceIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            ProviderTrace: trace,
            Assumptions: selected.Assumptions.Distinct(StringComparer.Ordinal).Take(256).ToArray(),
            OpenQuestions: selected.OpenQuestions.Distinct(StringComparer.Ordinal).Take(256).ToArray(),
            CreatedAt: now,
            UpdatedAt: now);
        await _repository.StoreCandidateAsync(candidate, cancellationToken);
        return candidate;
    }

    /// <summary>
    /// Validation is an explicit second phase. Trusted promotion is impossible without an independent validation record.
    /// </summary>
    public async Task<CandidateKnowledge> ValidateAndPromoteAsync(
        string knowledgeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(knowledgeId) || knowledgeId.Length > 160)
        {
            throw new ArgumentException("Knowledge id is invalid.", nameof(knowledgeId));
        }
        var candidate = await _repository.LoadAsync(knowledgeId, cancellationToken)
            ?? throw new KeyNotFoundException("Candidate knowledge was not found.");
        if (candidate.TrustState is not KnowledgeTrustState.Candidate)
        {
            throw new InvalidOperationException("Only candidate knowledge can enter the validation pipeline.");
        }

        var validation = (await _validator.ValidateAsync(candidate, cancellationToken)).Validate();
        if (!string.Equals(validation.KnowledgeId, candidate.KnowledgeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Knowledge validation record is bound to a different candidate.");
        }
        if (validation.ValidatedEvidenceIds.Except(candidate.EvidenceIds, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException("Knowledge validation referenced evidence outside the candidate evidence set.");
        }
        await _repository.StoreValidationAsync(validation, cancellationToken);

        var targetState = validation.EligibleForTrustedPromotion
            ? KnowledgeTrustState.Trusted
            : validation.EvidenceIntegrityPassed && validation.EvidenceSupportsStatement
                ? KnowledgeTrustState.Validated
                : KnowledgeTrustState.Rejected;
        await _repository.PromoteAsync(
            candidate.KnowledgeId,
            targetState,
            validation.ValidationRecordId,
            _time.GetUtcNow(),
            cancellationToken);

        return candidate with
        {
            TrustState = targetState,
            ValidationRecordId = validation.ValidationRecordId,
            UpdatedAt = _time.GetUtcNow()
        };
    }

    private static ModelAnalysisCandidate Consolidate(ModelAnalysisCandidate primary, ModelAnalysisCandidate secondary)
    {
        var sharedEvidence = primary.EvidenceIds
            .Intersect(secondary.EvidenceIds, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var allEvidence = primary.EvidenceIds
            .Concat(secondary.EvidenceIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var confidencePenalty = sharedEvidence.Length == 0 ? 0.15 : 0.05;
        var confidence = Math.Clamp(Math.Min(primary.Confidence, secondary.Confidence) - confidencePenalty, 0, 1);
        var statement = string.Equals(primary.Statement.Trim(), secondary.Statement.Trim(), StringComparison.Ordinal)
            ? primary.Statement.Trim()
            : $"Primary candidate: {primary.Statement.Trim()}\nSecondary candidate: {secondary.Statement.Trim()}";
        return new ModelAnalysisCandidate(
            ProviderId: "judge-consolidation",
            ProviderModelVersion: $"{primary.ProviderId}:{primary.ProviderModelVersion}|{secondary.ProviderId}:{secondary.ProviderModelVersion}",
            Statement: statement,
            Confidence: confidence,
            Risk: (ModelRiskLevel)Math.Max((int)primary.Risk, (int)secondary.Risk),
            EvidenceIds: allEvidence,
            Assumptions: primary.Assumptions.Concat(secondary.Assumptions).Distinct(StringComparer.Ordinal).Take(256).ToArray(),
            OpenQuestions: primary.OpenQuestions.Concat(secondary.OpenQuestions).Distinct(StringComparer.Ordinal).Take(256).ToArray());
    }

    private static void EnsureEvidenceSubset(AnalysisTask task, ModelAnalysisCandidate candidate)
    {
        var allowed = task.EvidenceIds.ToHashSet(StringComparer.Ordinal);
        var unknown = candidate.EvidenceIds.Where(id => !allowed.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidDataException("Model candidate referenced evidence outside the governed task context.");
        }
    }

    private static string Trace(ModelAnalysisCandidate candidate) =>
        $"{candidate.ProviderId}@{candidate.ProviderModelVersion}:{candidate.Confidence:0.000}:{candidate.Risk}";

    private static string BuildKnowledgeId(AnalysisTask task, ModelAnalysisCandidate candidate)
    {
        var canonical = string.Join("\n", new[]
        {
            task.ProjectId.ToString("D"),
            task.TargetId,
            task.TaskId,
            candidate.Statement.Trim(),
            string.Join("|", candidate.EvidenceIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "KN-" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
