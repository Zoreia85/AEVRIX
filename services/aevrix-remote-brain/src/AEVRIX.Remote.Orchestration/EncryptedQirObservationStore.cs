using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Remote.Orchestration;

public interface IProjectQirObservationStore
{
    Task StoreAsync(QirLearningObservation observation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QirLearningObservation>> LoadProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class EncryptedQirObservationStore : IProjectQirObservationStore
{
    private const int EnvelopeVersion = 1;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxEnvelopeBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly IProjectKnowledgeKeyProvider _keys;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public EncryptedQirObservationStore(string rootDirectory, IProjectKnowledgeKeyProvider keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    public async Task StoreAsync(QirLearningObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var path = RecordPath(observation.ProjectId, observation.ObservationId);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                var existing = await ReadAsync(path, observation.ProjectId, cancellationToken);
                if (!string.Equals(Fingerprint(existing), Fingerprint(observation), StringComparison.Ordinal))
                    throw new InvalidOperationException("QIR observation id is immutable in persistent storage.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteNewAsync(path, observation, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<QirLearningObservation>> LoadProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        var directory = ProjectDirectory(projectId);
        if (!Directory.Exists(directory)) return Array.Empty<QirLearningObservation>();

        var items = new List<QirLearningObservation>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.qir", SearchOption.TopDirectoryOnly)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await ReadAsync(path, projectId, cancellationToken));
        }

        return items.OrderBy(x => x.ObservedAt).ThenBy(x => x.ObservationId, StringComparer.Ordinal).ToArray();
    }

    private async Task WriteNewAsync(string path, QirLearningObservation observation, CancellationToken cancellationToken)
    {
        var key = await GetKeyCopyAsync(observation.ProjectId, cancellationToken);
        byte[]? plaintext = null;
        byte[]? envelopeBytes = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(observation, JsonOptions);
            if (plaintext.Length > MaxEnvelopeBytes / 2) throw new InvalidDataException("QIR payload exceeds safe bound.");

            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagBytes];
            using (var aes = new AesGcm(key, TagBytes))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(observation.ProjectId, observation.ObservationId));

            envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
                new QirEnvelope(EnvelopeVersion, observation.ProjectId, observation.ObservationId, nonce, ciphertext, tag), JsonOptions);
            if (envelopeBytes.Length > MaxEnvelopeBytes) throw new InvalidDataException("QIR envelope exceeds safe bound.");

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, envelopeBytes, cancellationToken);
                File.Move(temp, path, overwrite: false);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (envelopeBytes is not null) CryptographicOperations.ZeroMemory(envelopeBytes);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<QirLearningObservation> ReadAsync(string path, Guid expectedProjectId, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length is < 1 or > MaxEnvelopeBytes) throw new InvalidDataException("QIR envelope size is invalid.");

        QirEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<QirEnvelope>(bytes, JsonOptions)
                ?? throw new InvalidDataException("QIR envelope is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("QIR envelope is malformed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        ValidateEnvelope(envelope, expectedProjectId);
        var key = await GetKeyCopyAsync(expectedProjectId, cancellationToken);
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using (var aes = new AesGcm(key, TagBytes))
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext,
                    AssociatedData(expectedProjectId, envelope.ObservationId));

            QirLearningObservation observation;
            try
            {
                observation = JsonSerializer.Deserialize<QirLearningObservation>(plaintext, JsonOptions)
                    ?? throw new InvalidDataException("QIR payload is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("QIR decrypted payload is malformed.", ex);
            }

            observation.Validate();
            if (observation.ProjectId != expectedProjectId
                || !string.Equals(observation.ObservationId, envelope.ObservationId, StringComparison.Ordinal))
                throw new InvalidDataException("QIR payload binding does not match its authenticated envelope.");
            return observation;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidDataException("QIR authentication failed; data or project key may be incorrect.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<byte[]> GetKeyCopyAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var material = await _keys.GetKeyAsync(projectId, cancellationToken);
        if (material.Length != KeyBytes) throw new InvalidDataException("QIR project key must be exactly 256 bits.");
        return material.ToArray();
    }

    private void ValidateEnvelope(QirEnvelope envelope, Guid expectedProjectId)
    {
        if (envelope.Version != EnvelopeVersion || envelope.ProjectId != expectedProjectId)
            throw new InvalidDataException("QIR envelope version or project binding is invalid.");
        QirLearningObservation.ValidateId(envelope.ObservationId, 3, 160);
        if (envelope.Nonce?.Length != NonceBytes || envelope.Tag?.Length != TagBytes || envelope.Ciphertext is null)
            throw new InvalidDataException("QIR cryptographic envelope is invalid.");
        if (envelope.Ciphertext.Length > MaxEnvelopeBytes / 2)
            throw new InvalidDataException("QIR ciphertext exceeds safe bound.");
    }

    private string ProjectDirectory(Guid projectId) => Path.Combine(_root, "p-" + Hash(projectId.ToString("D")));
    private string RecordPath(Guid projectId, string observationId) =>
        Path.Combine(ProjectDirectory(projectId), "o-" + Hash(observationId) + ".qir");

    private static byte[] AssociatedData(Guid projectId, string observationId) =>
        Encoding.UTF8.GetBytes($"AEVRIX-QIR\n{EnvelopeVersion}\n{projectId:D}\n{observationId}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Fingerprint(QirLearningObservation x)
    {
        var canonical = string.Join("\n", x.ProjectId.ToString("D"), x.ObservationId, x.PatternKey,
            x.FeatureHash.ToLowerInvariant(), x.Basis, x.Sensitivity,
            x.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string.Join("|", x.EvidenceIds.OrderBy(id => id, StringComparer.Ordinal)),
            x.ObservedAt.ToUniversalTime().ToString("O"), x.ContainsPersonalData, x.ContainsRawSecretMaterial);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record QirEnvelope(
        int Version,
        Guid ProjectId,
        string ObservationId,
        byte[] Nonce,
        byte[] Ciphertext,
        byte[] Tag);
}

public sealed class PersistentQirLearningLedger
{
    private readonly QirLearningLedger _ledger;
    private readonly IProjectQirObservationStore _store;

    public PersistentQirLearningLedger(
        IProjectQirObservationStore store,
        QirLearningPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ledger = new QirLearningLedger(policy, timeProvider);
    }

    public async Task<QirLearningObservation> RecordAsync(
        QirLearningObservation observation,
        CancellationToken cancellationToken = default)
    {
        observation.Validate();
        await _store.StoreAsync(observation, cancellationToken);
        return _ledger.Record(observation);
    }

    public async Task<int> HydrateProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var observations = await _store.LoadProjectAsync(projectId, cancellationToken);
        foreach (var observation in observations) _ledger.Record(observation);
        return observations.Count;
    }

    public IReadOnlyList<QirLearningObservation> ProjectSnapshot(Guid projectId) => _ledger.ProjectSnapshot(projectId);
    public QirGlobalPattern Promote(string patternKey, string featureHash) => _ledger.Promote(patternKey, featureHash);
}
