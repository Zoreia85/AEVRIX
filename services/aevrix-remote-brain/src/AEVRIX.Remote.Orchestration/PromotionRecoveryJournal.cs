using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Remote.Orchestration;

public enum PromotionRecoveryState
{
    Prepared,
    Applied,
    LedgerCommitted
}

public sealed record PromotionExecutionReceipt(
    string OperationId,
    string PromotionReference,
    DateTimeOffset AppliedAt);

public interface IRecoverablePromotionExecutor
{
    Task<PromotionExecutionReceipt?> QueryAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Must treat operationId as the idempotency key and return the same logical receipt when the
    /// same already-applied operation is presented again.
    /// </summary>
    Task<PromotionExecutionReceipt> ExecuteAsync(
        string operationId,
        PromotionAuthorityAttestation attestation,
        PromotionEvidenceEnvelope evidence,
        CancellationToken cancellationToken = default);
}

public sealed record PromotionRecoveryRecord(
    int Version,
    string OperationId,
    PromotionRecoveryState State,
    string EvidenceDigestSha256,
    long AuthorizedHeadEntryCount,
    string AuthorizedHeadHashSha256,
    string CommitEventId,
    string? PromotionReferenceSha256,
    string? CommitRecordHashSha256,
    DateTimeOffset PreparedAt,
    DateTimeOffset? AppliedAt,
    DateTimeOffset? LedgerCommittedAt);

public interface IPromotionRecoveryJournal
{
    string ComputeOperationId(PromotionEvidenceEnvelope evidence);

    Task<PromotionRecoveryRecord?> LoadAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    Task<PromotionRecoveryRecord> PrepareAsync(
        PromotionEvidenceEnvelope evidence,
        string commitEventId,
        DateTimeOffset preparedAt,
        CancellationToken cancellationToken = default);

    Task<PromotionRecoveryRecord> MarkAppliedAsync(
        string operationId,
        PromotionExecutionReceipt receipt,
        CancellationToken cancellationToken = default);

    Task<PromotionRecoveryRecord> MarkLedgerCommittedAsync(
        string operationId,
        string commitRecordHashSha256,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Content-minimizing durable recovery journal. Filenames are HMAC-SHA-256 operation identifiers,
/// not raw execution metadata. Records are authenticated with a second independent 256-bit key and
/// are written through a same-directory temporary file + flush + atomic rename. The journal never
/// stores the external promotion reference itself; recovery obtains that from the idempotent executor.
/// </summary>
public sealed class FileBackedPromotionRecoveryJournal : IPromotionRecoveryJournal, IDisposable
{
    private const int CurrentVersion = 1;
    private const string OperationDomain = "AEVRIX_PROMOTION_OPERATION_V1\n";
    private const string JournalDomain = "AEVRIX_PROMOTION_JOURNAL_V1\n";
    private const int KeyBytes = 32;
    private const int MaximumJournalBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly byte[] _operationKey;
    private readonly byte[] _integrityKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileBackedPromotionRecoveryJournal(
        string rootDirectory,
        ReadOnlySpan<byte> operationIdKey,
        ReadOnlySpan<byte> integrityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (operationIdKey.Length != KeyBytes) throw new ArgumentException("Promotion operation-id key must be 256 bits.", nameof(operationIdKey));
        if (integrityKey.Length != KeyBytes) throw new ArgumentException("Promotion journal integrity key must be 256 bits.", nameof(integrityKey));
        if (CryptographicOperations.FixedTimeEquals(operationIdKey, integrityKey))
            throw new ArgumentException("Promotion journal requires independent operation-id and integrity keys.");

        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
        RejectReparsePoint(_root);
        _operationKey = operationIdKey.ToArray();
        _integrityKey = integrityKey.ToArray();
    }

    public string ComputeOperationId(PromotionEvidenceEnvelope evidence)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(evidence);
        var digest = evidence.ComputeDigestSha256();
        var bytes = Encoding.UTF8.GetBytes(OperationDomain + digest.ToLowerInvariant());
        try
        {
            using var hmac = new HMACSHA256(_operationKey);
            return Convert.ToHexString(hmac.ComputeHash(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<PromotionRecoveryRecord?> LoadAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PromotionRecoveryRecord> PrepareAsync(
        PromotionEvidenceEnvelope evidence,
        string commitEventId,
        DateTimeOffset preparedAt,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(evidence);
        ExecutionProofEvent.ValidateSafeId(commitEventId, nameof(commitEventId), 3, 160);
        if (preparedAt == default) throw new ArgumentException("Prepared timestamp is required.", nameof(preparedAt));

        var operationId = ComputeOperationId(evidence);
        var candidate = new PromotionRecoveryRecord(
            CurrentVersion,
            operationId,
            PromotionRecoveryState.Prepared,
            evidence.ComputeDigestSha256(),
            evidence.LedgerHead.EntryCount,
            evidence.LedgerHead.HeadHashSha256.ToLowerInvariant(),
            commitEventId,
            null,
            null,
            preparedAt.ToUniversalTime(),
            null,
            null);
        ValidateRecord(candidate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadCoreAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureSameIdentity(existing, candidate);
                return existing;
            }

            await WriteCoreAsync(candidate, cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PromotionRecoveryRecord> MarkAppliedAsync(
        string operationId,
        PromotionExecutionReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.Equals(receipt.OperationId, operationId, StringComparison.Ordinal))
            throw new InvalidDataException("Promotion execution receipt operation id does not match its journal.");
        ExecutionProofEvent.ValidateSafeId(receipt.PromotionReference, nameof(receipt.PromotionReference), 3, 160);
        if (receipt.AppliedAt == default) throw new InvalidDataException("Promotion execution receipt timestamp is missing.");
        var referenceHash = HashText(receipt.PromotionReference);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireCoreAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current.State == PromotionRecoveryState.LedgerCommitted)
                throw new InvalidOperationException("A ledger-committed promotion journal cannot transition back to Applied.");
            if (current.State == PromotionRecoveryState.Applied)
            {
                if (!FixedHexEquals(current.PromotionReferenceSha256!, referenceHash)
                    || current.AppliedAt != receipt.AppliedAt.ToUniversalTime())
                    throw new InvalidDataException("Promotion executor returned a different receipt for an already-applied operation.");
                return current;
            }

            var next = current with
            {
                State = PromotionRecoveryState.Applied,
                PromotionReferenceSha256 = referenceHash,
                AppliedAt = receipt.AppliedAt.ToUniversalTime()
            };
            ValidateRecord(next);
            await WriteCoreAsync(next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PromotionRecoveryRecord> MarkLedgerCommittedAsync(
        string operationId,
        string commitRecordHashSha256,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ExecutionProofEvent.ValidateSha256(commitRecordHashSha256, nameof(commitRecordHashSha256), required: true);
        if (committedAt == default) throw new ArgumentException("Ledger commit timestamp is required.", nameof(committedAt));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireCoreAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current.State == PromotionRecoveryState.Prepared)
                throw new InvalidOperationException("Promotion journal cannot become LedgerCommitted before the external effect is Applied.");
            if (current.State == PromotionRecoveryState.LedgerCommitted)
            {
                if (!FixedHexEquals(current.CommitRecordHashSha256!, commitRecordHashSha256))
                    throw new InvalidDataException("Promotion journal already commits a different execution-proof record.");
                return current;
            }

            var next = current with
            {
                State = PromotionRecoveryState.LedgerCommitted,
                CommitRecordHashSha256 = commitRecordHashSha256.ToLowerInvariant(),
                LedgerCommittedAt = committedAt.ToUniversalTime()
            };
            ValidateRecord(next);
            await WriteCoreAsync(next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PromotionRecoveryRecord> RequireCoreAsync(string operationId, CancellationToken cancellationToken) =>
        await LoadCoreAsync(operationId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Promotion recovery journal does not contain the required Prepared operation.");

    private async Task<PromotionRecoveryRecord?> LoadCoreAsync(string operationId, CancellationToken cancellationToken)
    {
        RejectReparsePoint(_root);
        var path = JournalPath(operationId);
        if (!File.Exists(path)) return null;
        RejectReparsePoint(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length is <= 0 or > MaximumJournalBytes)
            throw new InvalidDataException("Promotion recovery journal exceeds its bounded size.");
        try
        {
            var envelope = JsonSerializer.Deserialize<JournalEnvelope>(bytes, Json)
                ?? throw new InvalidDataException("Promotion recovery journal is malformed.");
            var record = envelope.Record ?? throw new InvalidDataException("Promotion recovery journal record is missing.");
            ValidateRecord(record);
            if (!string.Equals(record.OperationId, operationId, StringComparison.Ordinal))
                throw new InvalidDataException("Promotion recovery journal filename identity does not match its authenticated record.");
            var expected = ComputeRecordMac(record);
            try
            {
                if (!FixedHexEquals(envelope.MacSha256, expected))
                    throw new CryptographicException("Promotion recovery journal integrity check failed.");
            }
            finally
            {
                // strings are immutable; the key and transient binary MAC comparisons are protected separately.
            }
            return record;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task WriteCoreAsync(PromotionRecoveryRecord record, CancellationToken cancellationToken)
    {
        ValidateRecord(record);
        RejectReparsePoint(_root);
        var path = JournalPath(record.OperationId);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var envelope = new JournalEnvelope(record, ComputeRecordMac(record));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        if (bytes.Length > MaximumJournalBytes)
            throw new InvalidDataException("Promotion recovery journal exceeds its bounded size.");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private string ComputeRecordMac(PromotionRecoveryRecord record)
    {
        var canonical = string.Join("\n", new[]
        {
            JournalDomain.TrimEnd('\n'),
            record.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.OperationId,
            record.State.ToString(),
            record.EvidenceDigestSha256.ToLowerInvariant(),
            record.AuthorizedHeadEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.AuthorizedHeadHashSha256.ToLowerInvariant(),
            record.CommitEventId,
            record.PromotionReferenceSha256 ?? string.Empty,
            record.CommitRecordHashSha256 ?? string.Empty,
            record.PreparedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            record.AppliedAt?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            record.LedgerCommittedAt?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        });
        var bytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            using var hmac = new HMACSHA256(_integrityKey);
            return Convert.ToHexString(hmac.ComputeHash(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void EnsureSameIdentity(PromotionRecoveryRecord existing, PromotionRecoveryRecord candidate)
    {
        if (existing.Version != candidate.Version
            || !string.Equals(existing.OperationId, candidate.OperationId, StringComparison.Ordinal)
            || !FixedHexEquals(existing.EvidenceDigestSha256, candidate.EvidenceDigestSha256)
            || existing.AuthorizedHeadEntryCount != candidate.AuthorizedHeadEntryCount
            || !FixedHexEquals(existing.AuthorizedHeadHashSha256, candidate.AuthorizedHeadHashSha256)
            || !string.Equals(existing.CommitEventId, candidate.CommitEventId, StringComparison.Ordinal))
            throw new InvalidDataException("Promotion recovery operation id is already bound to different authorization evidence.");
    }

    private static void ValidateRecord(PromotionRecoveryRecord record)
    {
        if (record.Version != CurrentVersion) throw new InvalidDataException("Unsupported promotion recovery journal version.");
        ValidateOperationId(record.OperationId);
        ExecutionProofEvent.ValidateSha256(record.EvidenceDigestSha256, nameof(record.EvidenceDigestSha256), required: true);
        if (record.AuthorizedHeadEntryCount <= 0) throw new InvalidDataException("Promotion recovery authorized head must be non-empty.");
        ExecutionProofEvent.ValidateSha256(record.AuthorizedHeadHashSha256, nameof(record.AuthorizedHeadHashSha256), required: true);
        ExecutionProofEvent.ValidateSafeId(record.CommitEventId, nameof(record.CommitEventId), 3, 160);
        if (record.PreparedAt == default) throw new InvalidDataException("Promotion recovery prepared timestamp is missing.");

        if (record.State == PromotionRecoveryState.Prepared)
        {
            if (record.PromotionReferenceSha256 is not null || record.CommitRecordHashSha256 is not null
                || record.AppliedAt is not null || record.LedgerCommittedAt is not null)
                throw new InvalidDataException("Prepared promotion recovery record contains fields from a later state.");
            return;
        }

        ExecutionProofEvent.ValidateSha256(record.PromotionReferenceSha256, nameof(record.PromotionReferenceSha256), required: true);
        if (record.AppliedAt is null) throw new InvalidDataException("Applied promotion recovery record is missing its timestamp.");
        if (record.State == PromotionRecoveryState.Applied)
        {
            if (record.CommitRecordHashSha256 is not null || record.LedgerCommittedAt is not null)
                throw new InvalidDataException("Applied promotion recovery record contains fields from LedgerCommitted.");
            return;
        }

        ExecutionProofEvent.ValidateSha256(record.CommitRecordHashSha256, nameof(record.CommitRecordHashSha256), required: true);
        if (record.LedgerCommittedAt is null) throw new InvalidDataException("LedgerCommitted promotion recovery record is missing its timestamp.");
    }

    private string JournalPath(string operationId) => Path.Combine(_root, operationId + ".journal");

    private static void ValidateOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length != 64 || !operationId.All(Uri.IsHexDigit))
            throw new ArgumentException("Promotion operation id must be a SHA-256-sized lowercase/uppercase hexadecimal token.", nameof(operationId));
    }

    private static string HashText(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool FixedHexEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64 || !left.All(Uri.IsHexDigit) || !right.All(Uri.IsHexDigit)) return false;
        var a = Convert.FromHexString(left);
        var b = Convert.FromHexString(right);
        try { return CryptographicOperations.FixedTimeEquals(a, b); }
        finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Promotion recovery journal path must not be a symbolic link or reparse point.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_operationKey);
        CryptographicOperations.ZeroMemory(_integrityKey);
        _gate.Dispose();
    }

    private sealed record JournalEnvelope(PromotionRecoveryRecord? Record, string MacSha256);
}
