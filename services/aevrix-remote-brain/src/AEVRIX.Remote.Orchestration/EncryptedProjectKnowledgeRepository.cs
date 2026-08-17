using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Remote.Orchestration;

public interface IProjectKnowledgeKeyProvider
{
    ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class EncryptedProjectKnowledgeRepository : ICandidateKnowledgeRepository
{
    private const int CurrentEnvelopeVersion = 1;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int MaxEnvelopeBytes = 8 * 1024 * 1024;
    private const string CandidateKind = "candidate";
    private const string ValidationKind = "validation";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly IProjectKnowledgeKeyProvider _keys;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public EncryptedProjectKnowledgeRepository(string rootDirectory, IProjectKnowledgeKeyProvider keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    public async Task StoreCandidateAsync(CandidateKnowledge candidate, CancellationToken cancellationToken = default)
    {
        ValidateCandidate(candidate);
        var gate = LockFor(candidate.KnowledgeId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existingRecord = await TryReadRecordAsync<CandidateKnowledge>(CandidateKind, candidate.KnowledgeId, cancellationToken);
            if (existingRecord is not null)
            {
                EnsureCandidateProjectBinding(existingRecord);
                if (!SerializedEquivalent(existingRecord.Value, candidate))
                    throw new InvalidOperationException("Knowledge id is immutable and cannot be rebound to different candidate content.");
                return;
            }

            await WriteAsync(CandidateKind, candidate.KnowledgeId, candidate.ProjectId, candidate, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CandidateKnowledge?> LoadAsync(string knowledgeId, CancellationToken cancellationToken = default)
    {
        ValidateId(knowledgeId, nameof(knowledgeId));
        var record = await TryReadRecordAsync<CandidateKnowledge>(CandidateKind, knowledgeId, cancellationToken);
        if (record is null) return null;
        EnsureCandidateProjectBinding(record);
        return record.Value;
    }

    public async Task StoreValidationAsync(KnowledgeValidationRecord validation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validation);
        validation.Validate();
        ValidateId(validation.ValidationRecordId, nameof(validation.ValidationRecordId));
        ValidateId(validation.KnowledgeId, nameof(validation.KnowledgeId));

        var candidate = await LoadAsync(validation.KnowledgeId, cancellationToken)
            ?? throw new KeyNotFoundException("Validation cannot be stored because its candidate knowledge does not exist.");
        if (validation.ValidatedEvidenceIds.Except(candidate.EvidenceIds, StringComparer.Ordinal).Any())
            throw new InvalidDataException("Validation references evidence outside the authoritative candidate evidence set.");

        var gate = LockFor($"validation:{validation.ValidationRecordId}");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existingRecord = await TryReadRecordAsync<KnowledgeValidationRecord>(ValidationKind, validation.ValidationRecordId, cancellationToken);
            if (existingRecord is not null)
            {
                if (existingRecord.ProjectId != candidate.ProjectId)
                    throw new InvalidDataException("Validation record envelope is bound to a different project.");
                if (!SerializedEquivalent(existingRecord.Value, validation))
                    throw new InvalidOperationException("Validation record id is immutable and cannot be rebound.");
                return;
            }

            await WriteAsync(ValidationKind, validation.ValidationRecordId, candidate.ProjectId, validation, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ApplyValidationOutcomeAsync(
        string knowledgeId,
        KnowledgeTrustState state,
        string validationRecordId,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateId(knowledgeId, nameof(knowledgeId));
        ValidateId(validationRecordId, nameof(validationRecordId));
        if (decidedAt == default) throw new ArgumentOutOfRangeException(nameof(decidedAt));
        if (state is KnowledgeTrustState.Candidate or KnowledgeTrustState.Trusted)
            throw new InvalidOperationException("Generic validation outcome cannot create Candidate or Trusted knowledge.");

        var gate = LockFor(knowledgeId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (candidate, validation) = await LoadPromotionInputsAsync(knowledgeId, validationRecordId, cancellationToken);
            if (validation.EligibleForTrustedPromotion)
                throw new InvalidOperationException("Trusted validation requires an opaque memory-admission authorization.");

            var expectedState = validation.EvidenceIntegrityPassed && validation.EvidenceSupportsStatement
                ? KnowledgeTrustState.Validated
                : KnowledgeTrustState.Rejected;
            if (state != expectedState)
                throw new InvalidOperationException("Requested validation state does not match the authoritative validation result.");

            await WriteAsync(
                CandidateKind,
                knowledgeId,
                candidate.ProjectId,
                candidate with { TrustState = state, ValidationRecordId = validationRecordId, UpdatedAt = decidedAt },
                cancellationToken,
                overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PromoteTrustedAsync(
        TrustedKnowledgeAdmissionAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.Validate();
        var gate = LockFor(authorization.KnowledgeId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (candidate, validation) = await LoadPromotionInputsAsync(
                authorization.KnowledgeId,
                authorization.ValidationRecordId,
                cancellationToken);

            if (candidate.ProjectId != authorization.ProjectId
                || !string.Equals(candidate.TargetId, authorization.TargetId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Trusted admission authorization is bound to a different project or target.");
            if (!validation.EligibleForTrustedPromotion)
                throw new InvalidOperationException("Trusted admission authorization references validation that is not Trusted-eligible.");
            if (authorization.AdmittedAt < validation.ValidatedAt)
                throw new InvalidDataException("Trusted admission cannot predate its independent validation record.");

            await WriteAsync(
                CandidateKind,
                candidate.KnowledgeId,
                candidate.ProjectId,
                candidate with
                {
                    TrustState = KnowledgeTrustState.Trusted,
                    ValidationRecordId = validation.ValidationRecordId,
                    UpdatedAt = authorization.AdmittedAt
                },
                cancellationToken,
                overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(CandidateKnowledge Candidate, KnowledgeValidationRecord Validation)> LoadPromotionInputsAsync(
        string knowledgeId,
        string validationRecordId,
        CancellationToken cancellationToken)
    {
        var candidateRecord = await TryReadRecordAsync<CandidateKnowledge>(CandidateKind, knowledgeId, cancellationToken)
            ?? throw new KeyNotFoundException("Candidate knowledge was not found for promotion.");
        EnsureCandidateProjectBinding(candidateRecord);
        var candidate = candidateRecord.Value;
        if (candidate.TrustState != KnowledgeTrustState.Candidate)
            throw new InvalidOperationException("Only Candidate knowledge may be promoted by this repository.");

        var validationRecord = await TryReadRecordAsync<KnowledgeValidationRecord>(ValidationKind, validationRecordId, cancellationToken)
            ?? throw new KeyNotFoundException("Promotion validation record was not found.");
        if (validationRecord.ProjectId != candidate.ProjectId)
            throw new InvalidDataException("Promotion validation record is bound to a different project.");
        var validation = validationRecord.Value;
        if (!string.Equals(validation.KnowledgeId, knowledgeId, StringComparison.Ordinal))
            throw new InvalidDataException("Promotion validation record belongs to different knowledge.");
        if (validation.ValidatedEvidenceIds.Except(candidate.EvidenceIds, StringComparer.Ordinal).Any())
            throw new InvalidDataException("Promotion validation references evidence outside the candidate boundary.");
        return (candidate, validation);
    }

    private async Task WriteAsync<T>(
        string kind,
        string recordId,
        Guid projectId,
        T value,
        CancellationToken cancellationToken,
        bool overwrite = false)
    {
        var key = await GetKeyCopyAsync(projectId, cancellationToken);
        byte[]? plaintext = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            if (plaintext.Length > MaxEnvelopeBytes / 2)
                throw new InvalidDataException("Knowledge vault payload exceeds the configured bound.");

            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagBytes];
            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(kind, recordId, projectId));
            }

            var envelope = new VaultEnvelope(CurrentEnvelopeVersion, projectId, nonce, ciphertext, tag);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (bytes.Length > MaxEnvelopeBytes)
                throw new InvalidDataException("Knowledge vault envelope exceeds the configured bound.");

            var path = RecordPath(kind, recordId);
            if (!overwrite && File.Exists(path))
                throw new IOException("Knowledge vault record already exists.");

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
                File.Move(temp, path, overwrite);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task<DecryptedRecord<T>?> TryReadRecordAsync<T>(
        string kind,
        string recordId,
        CancellationToken cancellationToken)
    {
        ValidateId(recordId, nameof(recordId));
        var path = RecordPath(kind, recordId);
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Length is <= 0 or > MaxEnvelopeBytes)
            throw new InvalidDataException("Knowledge vault envelope size is invalid.");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        VaultEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<VaultEnvelope>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Knowledge vault envelope is unreadable.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Knowledge vault envelope is malformed.", ex);
        }
        ValidateEnvelope(envelope);

        var key = await GetKeyCopyAsync(envelope.ProjectId, cancellationToken);
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            try
            {
                using var aes = new AesGcm(key, TagBytes);
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext,
                    AssociatedData(kind, recordId, envelope.ProjectId));
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new InvalidDataException("Knowledge vault integrity verification failed.", ex);
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(plaintext, JsonOptions)
                    ?? throw new InvalidDataException("Knowledge vault payload is unreadable.");
                return new DecryptedRecord<T>(envelope.ProjectId, value);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Knowledge vault payload is malformed.", ex);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask<byte[]> GetKeyCopyAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty)
            throw new InvalidDataException("Knowledge vault project id cannot be empty.");
        var material = await _keys.GetKeyAsync(projectId, cancellationToken);
        if (material.Length != KeyBytes)
            throw new InvalidDataException("Knowledge vault project key must be exactly 256 bits.");
        return material.ToArray();
    }

    private string RecordPath(string kind, string recordId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{recordId}"))).ToLowerInvariant();
        var directory = Path.Combine(_root, kind);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, digest + ".aevk");
    }

    private SemaphoreSlim LockFor(string id) => _locks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));

    private static byte[] AssociatedData(string kind, string recordId, Guid projectId) =>
        Encoding.UTF8.GetBytes($"AEVRIX-KNOWLEDGE-V1\n{kind}\n{recordId}\n{projectId:D}");

    private static void ValidateEnvelope(VaultEnvelope envelope)
    {
        if (envelope.Version != CurrentEnvelopeVersion
            || envelope.ProjectId == Guid.Empty
            || envelope.Nonce is not { Length: NonceBytes }
            || envelope.Tag is not { Length: TagBytes }
            || envelope.Ciphertext is null
            || envelope.Ciphertext.Length == 0
            || envelope.Ciphertext.Length > MaxEnvelopeBytes / 2)
            throw new InvalidDataException("Knowledge vault envelope failed structural validation.");
    }

    private static void EnsureCandidateProjectBinding(DecryptedRecord<CandidateKnowledge> record)
    {
        if (record.Value.ProjectId != record.ProjectId)
            throw new InvalidDataException("Candidate payload project id does not match its authenticated envelope.");
    }

    private static void ValidateCandidate(CandidateKnowledge candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateId(candidate.KnowledgeId, nameof(candidate.KnowledgeId));
        if (candidate.ProjectId == Guid.Empty
            || !MissionTaskSpec.IsSafeId(candidate.TargetId, 2, 128)
            || string.IsNullOrWhiteSpace(candidate.Statement)
            || candidate.Statement.Length > 64_000
            || !double.IsFinite(candidate.Confidence)
            || candidate.Confidence is < 0 or > 1
            || candidate.TrustState != KnowledgeTrustState.Candidate
            || candidate.EvidenceIds is null
            || candidate.EvidenceIds.Count is < 1 or > 2_000
            || candidate.EvidenceIds.Any(id => !MissionTaskSpec.IsSafeId(id, 3, 160))
            || candidate.ProviderTrace is null
            || candidate.ProviderTrace.Count > 256
            || candidate.ProviderTrace.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1_024)
            || candidate.Assumptions is null
            || candidate.Assumptions.Count > 256
            || candidate.Assumptions.Any(value => value is null || value.Length > 8_000)
            || candidate.OpenQuestions is null
            || candidate.OpenQuestions.Count > 256
            || candidate.OpenQuestions.Any(value => value is null || value.Length > 8_000)
            || candidate.CreatedAt == default
            || candidate.UpdatedAt == default
            || candidate.UpdatedAt < candidate.CreatedAt)
            throw new InvalidDataException("Candidate knowledge is invalid for vault storage.");
        if (candidate.ValidationRecordId is not null)
            throw new InvalidDataException("New Candidate knowledge cannot already contain a validation record id.");
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (!MissionTaskSpec.IsSafeId(value, 3, 160))
            throw new ArgumentException("Knowledge vault record id is invalid.", parameterName);
    }

    private static bool SerializedEquivalent<T>(T left, T right)
    {
        var leftBytes = JsonSerializer.SerializeToUtf8Bytes(left, JsonOptions);
        var rightBytes = JsonSerializer.SerializeToUtf8Bytes(right, JsonOptions);
        try
        {
            Span<byte> leftHash = stackalloc byte[32];
            Span<byte> rightHash = stackalloc byte[32];
            SHA256.HashData(leftBytes, leftHash);
            SHA256.HashData(rightBytes, rightHash);
            return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private sealed record DecryptedRecord<T>(Guid ProjectId, T Value);

    private sealed record VaultEnvelope(
        int Version,
        Guid ProjectId,
        byte[] Nonce,
        byte[] Ciphertext,
        byte[] Tag);
}
