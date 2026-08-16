using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Remote.Orchestration;

public sealed record StoredExecutionProofSnapshot(
    Guid ProjectId,
    IReadOnlyList<ExecutionProofRecord> Records,
    ExecutionProofHead Head);

public interface IExecutionProofStore
{
    Task<StoredExecutionProofSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SaveAsync(
        Guid projectId,
        IReadOnlyList<ExecutionProofRecord> records,
        ExecutionProofHead head,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Project-bound authenticated persistence for the execution proof ledger.
/// The outer envelope exposes only versioned cryptographic material; project/run/execution metadata
/// remains inside AES-256-GCM ciphertext and the on-disk project filename is a SHA-256 digest.
/// </summary>
public sealed class EncryptedExecutionProofStore : IExecutionProofStore
{
    private const int EnvelopeVersion = 1;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaximumEnvelopeBytes = 16 * 1024 * 1024;
    private const int MaximumRecords = 100_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly IProjectKnowledgeKeyProvider _keys;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedExecutionProofStore(string rootDirectory, IProjectKnowledgeKeyProvider keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(
        Guid projectId,
        IReadOnlyList<ExecutionProofRecord> records,
        ExecutionProofHead head,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(projectId);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(head);
        if (records.Count > MaximumRecords)
            throw new InvalidDataException("Execution proof snapshot exceeds the configured record bound.");
        if (records.Any(record => record.Event.ProjectId != projectId))
            throw new InvalidDataException("Execution proof persistence is project-bound and rejects mixed-project snapshots.");
        ExecutionProofLedger.VerifySnapshot(records, head);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = await GetKeyCopyAsync(projectId, cancellationToken).ConfigureAwait(false);
            byte[]? plaintext = null;
            byte[]? envelopeBytes = null;
            try
            {
                plaintext = JsonSerializer.SerializeToUtf8Bytes(
                    new StoredPayload(EnvelopeVersion, projectId, records.ToArray(), head), Json);
                if (plaintext.Length > MaximumEnvelopeBytes / 2)
                    throw new InvalidDataException("Execution proof plaintext snapshot exceeds the configured size bound.");

                var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagBytes];
                using (var aes = new AesGcm(key, TagBytes))
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(projectId));

                envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new EncryptedEnvelope(EnvelopeVersion, nonce, ciphertext, tag), Json);
                if (envelopeBytes.Length > MaximumEnvelopeBytes)
                    throw new InvalidDataException("Execution proof encrypted envelope exceeds the configured size bound.");

                var path = PathFor(projectId);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temp, envelopeBytes, cancellationToken).ConfigureAwait(false);
                    File.Move(temp, path, overwrite: true);
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
                if (envelopeBytes is not null) CryptographicOperations.ZeroMemory(envelopeBytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredExecutionProofSnapshot?> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(projectId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(projectId);
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length is <= 0 or > MaximumEnvelopeBytes)
                throw new InvalidDataException("Execution proof encrypted envelope size is invalid.");

            var envelopeBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            EncryptedEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(envelopeBytes, Json)
                    ?? throw new InvalidDataException("Execution proof encrypted envelope is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Execution proof encrypted envelope is malformed.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
            }

            if (envelope.Version != EnvelopeVersion
                || envelope.Nonce is not { Length: NonceBytes }
                || envelope.Tag is not { Length: TagBytes }
                || envelope.Ciphertext is null
                || envelope.Ciphertext.Length <= 0
                || envelope.Ciphertext.Length > MaximumEnvelopeBytes / 2)
                throw new InvalidDataException("Execution proof cryptographic envelope is invalid.");

            var key = await GetKeyCopyAsync(projectId, cancellationToken).ConfigureAwait(false);
            var plaintext = new byte[envelope.Ciphertext.Length];
            try
            {
                try
                {
                    using var aes = new AesGcm(key, TagBytes);
                    aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, Aad(projectId));
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    throw new InvalidDataException("Execution proof authentication failed; data or project key is not authoritative.", ex);
                }

                StoredPayload payload;
                try
                {
                    payload = JsonSerializer.Deserialize<StoredPayload>(plaintext, Json)
                        ?? throw new InvalidDataException("Execution proof decrypted payload is empty.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Execution proof decrypted payload is malformed.", ex);
                }

                if (payload.Version != EnvelopeVersion || payload.ProjectId != projectId
                    || payload.Records is null || payload.Head is null || payload.Records.Length > MaximumRecords)
                    throw new InvalidDataException("Execution proof decrypted project/version binding is invalid.");
                if (payload.Records.Any(record => record.Event.ProjectId != projectId))
                    throw new InvalidDataException("Execution proof decrypted snapshot crossed its authenticated project boundary.");

                ExecutionProofLedger.VerifySnapshot(payload.Records, payload.Head);
                return new StoredExecutionProofSnapshot(projectId, payload.Records, payload.Head);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> GetKeyCopyAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var material = await _keys.GetKeyAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (material.Length != KeyBytes)
            throw new InvalidDataException("Execution proof project key must be exactly 256 bits.");
        return material.ToArray();
    }

    private string PathFor(Guid projectId) =>
        Path.Combine(_root, "p-" + Hash(projectId.ToString("D")) + ".aevx");

    private static byte[] Aad(Guid projectId) =>
        Encoding.UTF8.GetBytes($"AEVRIX-EXECUTION-PROOF\n{EnvelopeVersion}\n{projectId:D}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateProject(Guid projectId)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
    }

    private sealed record EncryptedEnvelope(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
    private sealed record StoredPayload(
        int Version,
        Guid ProjectId,
        ExecutionProofRecord[] Records,
        ExecutionProofHead Head);
}

/// <summary>
/// Transactional project-bound facade. A candidate append is built on a verified clone, persisted,
/// and only then becomes the in-memory authoritative state. A failed persistence operation therefore
/// cannot silently advance the live ledger.
/// </summary>
public sealed class PersistentExecutionProofLedger
{
    private readonly Guid _projectId;
    private readonly IExecutionProofStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ExecutionProofLedger _ledger;

    private PersistentExecutionProofLedger(Guid projectId, IExecutionProofStore store, ExecutionProofLedger ledger)
    {
        _projectId = projectId;
        _store = store;
        _ledger = ledger;
    }

    public static async Task<PersistentExecutionProofLedger> OpenAsync(
        Guid projectId,
        IExecutionProofStore store,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        ArgumentNullException.ThrowIfNull(store);
        var snapshot = await store.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
        var ledger = snapshot is null
            ? new ExecutionProofLedger()
            : Rehydrate(snapshot.Records, snapshot.Head);
        return new PersistentExecutionProofLedger(projectId, store, ledger);
    }

    public ExecutionProofHead Head => _ledger.Head;

    public IReadOnlyList<ExecutionProofRecord> Snapshot() => _ledger.Snapshot();

    public async Task<ExecutionProofRecord> AppendAsync(
        ExecutionProofEvent item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ProjectId != _projectId)
            throw new InvalidDataException("Persistent execution proof ledger rejected a cross-project append.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidate = Rehydrate(_ledger.Snapshot(), _ledger.Head);
            var record = candidate.Append(item);
            await _store.SaveAsync(_projectId, candidate.Snapshot(), candidate.Head, cancellationToken).ConfigureAwait(false);
            _ledger = candidate;
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PromotionEvidenceEnvelope> BuildPromotionEvidenceAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _ledger.BuildPromotionEvidence(executionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ExecutionProofLedger Rehydrate(
        IReadOnlyList<ExecutionProofRecord> records,
        ExecutionProofHead head)
    {
        ExecutionProofLedger.VerifySnapshot(records, head);
        var ledger = new ExecutionProofLedger();
        foreach (var stored in records)
        {
            var rebuilt = ledger.Append(stored.Event);
            if (!string.Equals(rebuilt.RecordHashSha256, stored.RecordHashSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Execution proof deterministic rehydration did not reproduce the stored chain.");
        }
        if (ledger.Head != head)
            throw new InvalidDataException("Execution proof rehydration head does not match the authenticated stored head.");
        return ledger;
    }
}
