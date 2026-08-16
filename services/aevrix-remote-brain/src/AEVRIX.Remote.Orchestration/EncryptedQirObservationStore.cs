using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Remote.Orchestration;

public sealed class EncryptedQirObservationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly IProjectKnowledgeKeyProvider _keys;

    public EncryptedQirObservationStore(string root, IProjectKnowledgeKeyProvider keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        Directory.CreateDirectory(_root);
    }

    public async Task StoreAsync(QirLearningObservation item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        var path = PathFor(item.ProjectId, item.ObservationId);
        if (File.Exists(path))
        {
            var current = await ReadAsync(path, item.ProjectId, ct);
            if (Fingerprint(current) != Fingerprint(item)) throw new InvalidOperationException("QIR observation id is immutable.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = await KeyAsync(item.ProjectId, ct);
        var plain = JsonSerializer.SerializeToUtf8Bytes(item, Json);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, plain, cipher, tag, Aad(item.ProjectId, Path.GetFileName(path)));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, nonce, cipher, tag), Json);
            try { await File.WriteAllBytesAsync(path, bytes, ct); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<IReadOnlyList<QirLearningObservation>> LoadProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        var dir = ProjectDir(projectId);
        if (!Directory.Exists(dir)) return Array.Empty<QirLearningObservation>();
        var items = new List<QirLearningObservation>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.qir").OrderBy(x => x, StringComparer.Ordinal))
            items.Add(await ReadAsync(path, projectId, ct));
        return items.OrderBy(x => x.ObservedAt).ThenBy(x => x.ObservationId, StringComparer.Ordinal).ToArray();
    }

    private async Task<QirLearningObservation> ReadAsync(string path, Guid projectId, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        Envelope env;
        try { env = JsonSerializer.Deserialize<Envelope>(bytes, Json) ?? throw new InvalidDataException("QIR envelope is empty."); }
        catch (JsonException ex) { throw new InvalidDataException("QIR envelope is malformed.", ex); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (env.Version != 1 || env.Nonce?.Length != 12 || env.Tag?.Length != 16 || env.Ciphertext is null)
            throw new InvalidDataException("QIR envelope is invalid.");

        var key = await KeyAsync(projectId, ct);
        var plain = new byte[env.Ciphertext.Length];
        try
        {
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(env.Nonce, env.Ciphertext, env.Tag, plain, Aad(projectId, Path.GetFileName(path)));
            }
            catch (AuthenticationTagMismatchException ex) { throw new InvalidDataException("QIR authentication failed.", ex); }
            var item = JsonSerializer.Deserialize<QirLearningObservation>(plain, Json)
                ?? throw new InvalidDataException("QIR payload is empty.");
            item.Validate();
            if (item.ProjectId != projectId || path != PathFor(projectId, item.ObservationId))
                throw new InvalidDataException("QIR project binding is invalid.");
            return item;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<byte[]> KeyAsync(Guid projectId, CancellationToken ct)
    {
        var key = await _keys.GetKeyAsync(projectId, ct);
        if (key.Length != 32) throw new InvalidDataException("QIR project key must be 256 bits.");
        return key.ToArray();
    }

    private string ProjectDir(Guid id) => Path.Combine(_root, "p-" + Hash(id.ToString("D")));
    private string PathFor(Guid id, string observationId) => Path.Combine(ProjectDir(id), "o-" + Hash(observationId) + ".qir");
    private static byte[] Aad(Guid id, string file) => Encoding.UTF8.GetBytes($"AEVRIX-QIR\n1\n{id:D}\n{file}");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Fingerprint(QirLearningObservation x) => Hash(JsonSerializer.Serialize(x, Json));
    private sealed record Envelope(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}
