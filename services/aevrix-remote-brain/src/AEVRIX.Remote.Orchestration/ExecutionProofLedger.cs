using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

public enum ExecutionProofStage
{
    Started,
    Completed,
    ValidationCompleted,
    JudgeDecided,
    PromotionAuthorized,
    PromotionCommitted
}

public enum ExecutionProofOutcome
{
    Pending,
    Succeeded,
    Failed,
    Rejected,
    Approved,
    Committed
}

public sealed record ExecutionProofEvent(
    string EventId,
    Guid ProjectId,
    string RunId,
    string ExecutionId,
    ExecutionProofStage Stage,
    string CapabilityClass,
    string CapabilityId,
    ExecutionProofOutcome Outcome,
    string InputDigestSha256,
    string? AuthorityDigestSha256,
    string? ResultDigestSha256,
    string? AttestationDigestSha256,
    string? ArtifactManifestSha256,
    string? ValidationDigestSha256,
    string? JudgeDecisionDigestSha256,
    string? PromotionDigestSha256,
    string? PromotionReference,
    DateTimeOffset ObservedAt)
{
    public ExecutionProofEvent Validate()
    {
        ValidateSafeId(EventId, nameof(EventId), 3, 160);
        ValidateSafeId(RunId, nameof(RunId), 3, 160);
        ValidateSafeId(ExecutionId, nameof(ExecutionId), 3, 160);
        ValidateSafeId(CapabilityClass, nameof(CapabilityClass), 2, 80);
        ValidateSafeId(CapabilityId, nameof(CapabilityId), 2, 160);
        if (ProjectId == Guid.Empty) throw new InvalidDataException("Execution proof project id cannot be empty.");
        if (!Enum.IsDefined(Stage) || !Enum.IsDefined(Outcome)) throw new InvalidDataException("Execution proof enum value is invalid.");
        ValidateSha256(InputDigestSha256, nameof(InputDigestSha256), required: true);
        ValidateSha256(AuthorityDigestSha256, nameof(AuthorityDigestSha256));
        ValidateSha256(ResultDigestSha256, nameof(ResultDigestSha256));
        ValidateSha256(AttestationDigestSha256, nameof(AttestationDigestSha256));
        ValidateSha256(ArtifactManifestSha256, nameof(ArtifactManifestSha256));
        ValidateSha256(ValidationDigestSha256, nameof(ValidationDigestSha256));
        ValidateSha256(JudgeDecisionDigestSha256, nameof(JudgeDecisionDigestSha256));
        ValidateSha256(PromotionDigestSha256, nameof(PromotionDigestSha256));
        if (PromotionReference is not null) ValidateSafeId(PromotionReference, nameof(PromotionReference), 3, 160);
        if (ObservedAt == default) throw new InvalidDataException("Execution proof timestamp is missing.");
        return this;
    }

    internal static void ValidateSafeId(string value, string name, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length < min
            || value.Length > max
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':')))
        {
            throw new InvalidDataException($"Execution proof {name} is invalid.");
        }
    }

    internal static void ValidateSha256(string? value, string name, bool required = false)
    {
        if (value is null)
        {
            if (required) throw new InvalidDataException($"Execution proof {name} is required.");
            return;
        }
        if (value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException($"Execution proof {name} must be SHA-256 hexadecimal.");
    }
}

public sealed record ExecutionProofRecord(
    int Version,
    long Sequence,
    ExecutionProofEvent Event,
    string PreviousRecordHashSha256,
    string RecordHashSha256);

public sealed record ExecutionProofHead(
    long EntryCount,
    string HeadHashSha256)
{
    public static ExecutionProofHead Empty { get; } = new(0, ExecutionProofLedger.GenesisHash);
}

public sealed record PromotionEvidenceEnvelope(
    int Version,
    Guid ProjectId,
    string RunId,
    string ExecutionId,
    string CapabilityClass,
    string CapabilityId,
    string ArtifactManifestSha256,
    string ValidationDigestSha256,
    string JudgeDecisionDigestSha256,
    string PromotionDigestSha256,
    string AuthorizationRecordHashSha256,
    ExecutionProofHead LedgerHead)
{
    public string ComputeDigestSha256()
    {
        var canonical = string.Join("\n", new[]
        {
            Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ProjectId.ToString("D"), RunId, ExecutionId, CapabilityClass, CapabilityId,
            ArtifactManifestSha256.ToLowerInvariant(), ValidationDigestSha256.ToLowerInvariant(),
            JudgeDecisionDigestSha256.ToLowerInvariant(), PromotionDigestSha256.ToLowerInvariant(),
            AuthorizationRecordHashSha256.ToLowerInvariant(),
            LedgerHead.EntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            LedgerHead.HeadHashSha256.ToLowerInvariant()
        });
        return Hash(canonical);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>
/// Authoritative, content-minimizing execution ledger. It records only opaque identifiers,
/// bounded metadata and cryptographic digests. Prompts, raw model output, secrets and artifact
/// contents are deliberately outside this contract.
/// </summary>
public sealed class ExecutionProofLedger
{
    public const int CurrentVersion = 1;
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly object _sync = new();
    private readonly List<ExecutionProofRecord> _records = [];
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionState> _executions = new(StringComparer.Ordinal);

    public ExecutionProofHead Head
    {
        get
        {
            lock (_sync)
            {
                return HeadUnsafe();
            }
        }
    }

    public ExecutionProofRecord Append(ExecutionProofEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        lock (_sync)
        {
            if (!_eventIds.Add(item.EventId))
                throw new InvalidOperationException("Execution proof event id is immutable and cannot be reused.");

            try
            {
                ApplySemanticTransition(item, _executions);
                var previous = _records.Count == 0 ? GenesisHash : _records[^1].RecordHashSha256;
                var sequence = checked((long)_records.Count + 1);
                var hash = ComputeRecordHash(CurrentVersion, sequence, item, previous);
                var record = new ExecutionProofRecord(CurrentVersion, sequence, item, previous, hash);
                _records.Add(record);
                return record;
            }
            catch
            {
                _eventIds.Remove(item.EventId);
                RebuildExecutionStateUnsafe();
                throw;
            }
        }
    }

    public IReadOnlyList<ExecutionProofRecord> Snapshot()
    {
        lock (_sync)
        {
            return _records.ToArray();
        }
    }

    public PromotionEvidenceEnvelope BuildPromotionEvidence(string executionId)
    {
        ExecutionProofEvent.ValidateSafeId(executionId, nameof(executionId), 3, 160);
        lock (_sync)
        {
            var head = HeadUnsafe();
            VerifySnapshot(_records, head);
            if (!_executions.TryGetValue(executionId, out var state)
                || state.Authorization is null
                || state.Validation is null
                || state.Judge is null
                || state.Completed is null)
            {
                throw new InvalidOperationException("Execution has no complete validated Judge-backed promotion authorization.");
            }

            var authorization = state.Authorization;
            var authRecord = _records.Single(x => string.Equals(x.Event.EventId, authorization.EventId, StringComparison.Ordinal));
            return new PromotionEvidenceEnvelope(
                CurrentVersion,
                authorization.ProjectId,
                authorization.RunId,
                authorization.ExecutionId,
                authorization.CapabilityClass,
                authorization.CapabilityId,
                Require(authorization.ArtifactManifestSha256, "artifact manifest"),
                Require(authorization.ValidationDigestSha256, "validation digest"),
                Require(authorization.JudgeDecisionDigestSha256, "Judge digest"),
                Require(authorization.PromotionDigestSha256, "promotion digest"),
                authRecord.RecordHashSha256,
                head);
        }
    }

    public static void VerifySnapshot(
        IReadOnlyList<ExecutionProofRecord> records,
        ExecutionProofHead expectedHead)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(expectedHead);
        ExecutionProofEvent.ValidateSha256(expectedHead.HeadHashSha256, nameof(expectedHead.HeadHashSha256), required: true);
        if (expectedHead.EntryCount < 0) throw new InvalidDataException("Execution proof expected entry count cannot be negative.");
        if (records.Count != expectedHead.EntryCount)
            throw new InvalidDataException("Execution proof snapshot length does not match its externally retained head.");

        var expectedPrevious = GenesisHash;
        var states = new Dictionary<string, ExecutionState>(StringComparer.Ordinal);
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index] ?? throw new InvalidDataException("Execution proof snapshot contains a null record.");
            record.Event.Validate();
            if (record.Version != CurrentVersion || record.Sequence != index + 1L)
                throw new InvalidDataException("Execution proof record version or sequence is invalid.");
            if (!eventIds.Add(record.Event.EventId))
                throw new InvalidDataException("Execution proof snapshot contains a replayed event id.");
            if (!CryptographicEquals(record.PreviousRecordHashSha256, expectedPrevious))
                throw new InvalidDataException("Execution proof previous-record chain is broken.");

            var computed = ComputeRecordHash(record.Version, record.Sequence, record.Event, record.PreviousRecordHashSha256);
            if (!CryptographicEquals(record.RecordHashSha256, computed))
                throw new InvalidDataException("Execution proof record hash verification failed.");

            ApplySemanticTransition(record.Event, states);
            expectedPrevious = record.RecordHashSha256;
        }

        var actualHead = records.Count == 0 ? GenesisHash : records[^1].RecordHashSha256;
        if (!CryptographicEquals(actualHead, expectedHead.HeadHashSha256))
            throw new InvalidDataException("Execution proof head hash does not match the externally retained head.");
    }

    internal static string ComputeRecordHash(
        int version,
        long sequence,
        ExecutionProofEvent item,
        string previousHash)
    {
        item.Validate();
        ExecutionProofEvent.ValidateSha256(previousHash, nameof(previousHash), required: true);
        var canonical = string.Join("\n", new[]
        {
            version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            previousHash.ToLowerInvariant(), item.EventId, item.ProjectId.ToString("D"), item.RunId, item.ExecutionId,
            item.Stage.ToString(), item.CapabilityClass, item.CapabilityId, item.Outcome.ToString(),
            item.InputDigestSha256.ToLowerInvariant(), NormalizeHash(item.AuthorityDigestSha256),
            NormalizeHash(item.ResultDigestSha256), NormalizeHash(item.AttestationDigestSha256),
            NormalizeHash(item.ArtifactManifestSha256), NormalizeHash(item.ValidationDigestSha256),
            NormalizeHash(item.JudgeDecisionDigestSha256), NormalizeHash(item.PromotionDigestSha256),
            item.PromotionReference ?? string.Empty, item.ObservedAt.ToUniversalTime().ToString("O")
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private ExecutionProofHead HeadUnsafe() => _records.Count == 0
        ? ExecutionProofHead.Empty
        : new ExecutionProofHead(_records.Count, _records[^1].RecordHashSha256);

    private void RebuildExecutionStateUnsafe()
    {
        _executions.Clear();
        foreach (var record in _records) ApplySemanticTransition(record.Event, _executions);
    }

    private static void ApplySemanticTransition(
        ExecutionProofEvent item,
        IDictionary<string, ExecutionState> states)
    {
        if (!states.TryGetValue(item.ExecutionId, out var state))
        {
            if (item.Stage != ExecutionProofStage.Started)
                throw new InvalidDataException("Execution proof must begin with Started.");
            ValidateStarted(item);
            states[item.ExecutionId] = new ExecutionState(item);
            return;
        }

        EnsureBinding(state.Started, item);
        switch (item.Stage)
        {
            case ExecutionProofStage.Started:
                throw new InvalidDataException("Execution proof cannot contain a second Started event.");
            case ExecutionProofStage.Completed:
                if (state.Completed is not null) throw new InvalidDataException("Execution already has a Completed event.");
                ValidateCompleted(item, state.Started);
                state.Completed = item;
                break;
            case ExecutionProofStage.ValidationCompleted:
                if (state.Completed is null || state.Validation is not null)
                    throw new InvalidDataException("Validation requires exactly one prior completion.");
                ValidateValidation(item, state.Completed);
                state.Validation = item;
                break;
            case ExecutionProofStage.JudgeDecided:
                if (state.Validation is null || state.Judge is not null)
                    throw new InvalidDataException("Judge decision requires exactly one prior validation.");
                ValidateJudge(item, state.Validation);
                state.Judge = item;
                break;
            case ExecutionProofStage.PromotionAuthorized:
                if (state.Judge is null || state.Authorization is not null)
                    throw new InvalidDataException("Promotion authorization requires exactly one prior Judge decision.");
                ValidateAuthorization(item, state.Judge);
                state.Authorization = item;
                break;
            case ExecutionProofStage.PromotionCommitted:
                if (state.Authorization is null || state.Commit is not null)
                    throw new InvalidDataException("Promotion commit requires exactly one prior authorization.");
                ValidateCommit(item, state.Authorization);
                state.Commit = item;
                break;
            default:
                throw new InvalidDataException("Unknown execution proof stage.");
        }
    }

    private static void ValidateStarted(ExecutionProofEvent item)
    {
        if (item.Outcome != ExecutionProofOutcome.Pending
            || item.ResultDigestSha256 is not null || item.AttestationDigestSha256 is not null
            || item.ArtifactManifestSha256 is not null || item.ValidationDigestSha256 is not null
            || item.JudgeDecisionDigestSha256 is not null || item.PromotionDigestSha256 is not null
            || item.PromotionReference is not null)
            throw new InvalidDataException("Started execution proof contains premature result or promotion material.");
    }

    private static void ValidateCompleted(ExecutionProofEvent item, ExecutionProofEvent started)
    {
        if (item.Outcome is not (ExecutionProofOutcome.Succeeded or ExecutionProofOutcome.Failed))
            throw new InvalidDataException("Completed execution proof outcome must be Succeeded or Failed.");
        if (item.ResultDigestSha256 is null)
            throw new InvalidDataException("Completed execution proof requires a result digest.");
        if (item.ValidationDigestSha256 is not null || item.JudgeDecisionDigestSha256 is not null
            || item.PromotionDigestSha256 is not null || item.PromotionReference is not null)
            throw new InvalidDataException("Completed execution proof cannot contain later-stage material.");
        EnsureInputAndAuthority(started, item);
    }

    private static void ValidateValidation(ExecutionProofEvent item, ExecutionProofEvent completed)
    {
        if (completed.Outcome != ExecutionProofOutcome.Succeeded || completed.ArtifactManifestSha256 is null)
            throw new InvalidDataException("Promotion validation requires a successful execution with an artifact manifest.");
        if (item.Outcome is not (ExecutionProofOutcome.Succeeded or ExecutionProofOutcome.Rejected)
            || item.ValidationDigestSha256 is null)
            throw new InvalidDataException("Validation proof requires a validation digest and final validation outcome.");
        RequireCopiedExecutionProof(completed, item, includeValidation: false, includeJudge: false, includePromotion: false);
    }

    private static void ValidateJudge(ExecutionProofEvent item, ExecutionProofEvent validation)
    {
        if (validation.Outcome != ExecutionProofOutcome.Succeeded)
            throw new InvalidDataException("Judge cannot approve promotion after rejected validation.");
        if (item.Outcome is not (ExecutionProofOutcome.Approved or ExecutionProofOutcome.Rejected)
            || item.JudgeDecisionDigestSha256 is null)
            throw new InvalidDataException("Judge proof requires a decision digest and Approved/Rejected outcome.");
        RequireCopiedExecutionProof(validation, item, includeValidation: true, includeJudge: false, includePromotion: false);
    }

    private static void ValidateAuthorization(ExecutionProofEvent item, ExecutionProofEvent judge)
    {
        if (judge.Outcome != ExecutionProofOutcome.Approved || item.Outcome != ExecutionProofOutcome.Approved
            || item.PromotionDigestSha256 is null)
            throw new InvalidDataException("Promotion authorization requires an approved Judge decision and promotion digest.");
        RequireCopiedExecutionProof(judge, item, includeValidation: true, includeJudge: true, includePromotion: false);
    }

    private static void ValidateCommit(ExecutionProofEvent item, ExecutionProofEvent authorization)
    {
        if (item.Outcome != ExecutionProofOutcome.Committed || item.PromotionReference is null)
            throw new InvalidDataException("Promotion commit requires Committed outcome and a bounded promotion reference.");
        RequireCopiedExecutionProof(authorization, item, includeValidation: true, includeJudge: true, includePromotion: true);
    }

    private static void EnsureBinding(ExecutionProofEvent origin, ExecutionProofEvent item)
    {
        if (origin.ProjectId != item.ProjectId
            || !string.Equals(origin.RunId, item.RunId, StringComparison.Ordinal)
            || !string.Equals(origin.CapabilityClass, item.CapabilityClass, StringComparison.Ordinal)
            || !string.Equals(origin.CapabilityId, item.CapabilityId, StringComparison.Ordinal))
            throw new InvalidDataException("Execution proof attempted a cross-project/run/capability replay.");
    }

    private static void EnsureInputAndAuthority(ExecutionProofEvent origin, ExecutionProofEvent item)
    {
        if (!HashEquals(origin.InputDigestSha256, item.InputDigestSha256)
            || !NullableHashEquals(origin.AuthorityDigestSha256, item.AuthorityDigestSha256))
            throw new InvalidDataException("Execution proof input or authority digest changed during the execution.");
    }

    private static void RequireCopiedExecutionProof(
        ExecutionProofEvent previous,
        ExecutionProofEvent current,
        bool includeValidation,
        bool includeJudge,
        bool includePromotion)
    {
        EnsureInputAndAuthority(previous, current);
        if (!NullableHashEquals(previous.ResultDigestSha256, current.ResultDigestSha256)
            || !NullableHashEquals(previous.AttestationDigestSha256, current.AttestationDigestSha256)
            || !NullableHashEquals(previous.ArtifactManifestSha256, current.ArtifactManifestSha256)
            || (includeValidation && !NullableHashEquals(previous.ValidationDigestSha256, current.ValidationDigestSha256))
            || (includeJudge && !NullableHashEquals(previous.JudgeDecisionDigestSha256, current.JudgeDecisionDigestSha256))
            || (includePromotion && !NullableHashEquals(previous.PromotionDigestSha256, current.PromotionDigestSha256)))
            throw new InvalidDataException("Execution proof stage is not cryptographically bound to the prior stage.");
    }

    private static bool NullableHashEquals(string? left, string? right) =>
        left is null ? right is null : right is not null && HashEquals(left, right);

    private static bool HashEquals(string left, string right)
    {
        var a = Convert.FromHexString(left);
        var b = Convert.FromHexString(right);
        try { return CryptographicOperations.FixedTimeEquals(a, b); }
        finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
    }

    private static bool CryptographicEquals(string supplied, string expected)
    {
        ExecutionProofEvent.ValidateSha256(supplied, nameof(supplied), required: true);
        ExecutionProofEvent.ValidateSha256(expected, nameof(expected), required: true);
        return HashEquals(supplied, expected);
    }

    private static string NormalizeHash(string? value) => value?.ToLowerInvariant() ?? string.Empty;
    private static string Require(string? value, string name) =>
        value ?? throw new InvalidDataException($"Execution proof is missing {name}.");

    private sealed class ExecutionState(ExecutionProofEvent started)
    {
        public ExecutionProofEvent Started { get; } = started;
        public ExecutionProofEvent? Completed { get; set; }
        public ExecutionProofEvent? Validation { get; set; }
        public ExecutionProofEvent? Judge { get; set; }
        public ExecutionProofEvent? Authorization { get; set; }
        public ExecutionProofEvent? Commit { get; set; }
    }
}
