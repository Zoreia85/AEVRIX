using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Core;

public enum EvidenceClassification
{
    Quarantine,
    Sanitized,
    NeutralKnowledge
}

public enum EvidenceMetadataRetention
{
    Minimal,
    Full
}

public sealed record StoredEvidenceArtifact(
    string EvidenceId,
    Guid ProjectId,
    string CaptureId,
    EvidenceClassification Classification,
    string Kind,
    string OriginalName,
    string RelativePath,
    string Sha256,
    long SizeBytes,
    string MediaType,
    DateTimeOffset StoredAt,
    EvidenceBasis Basis,
    string? SourceUri = null,
    string? Description = null,
    string? CaptureRelativePath = null);

public sealed class EvidenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AevrixDataPaths _paths;
    private readonly EvidenceMetadataRetention _metadataRetention;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EvidenceStore(
        AevrixDataPaths paths,
        EvidenceMetadataRetention metadataRetention = EvidenceMetadataRetention.Minimal)
    {
        _paths = paths.EnsureCreated();
        _metadataRetention = metadataRetention;
    }

    public Task<StoredEvidenceArtifact> StoreFileAsync(
        Guid projectId,
        string captureId,
        string sourceFile,
        EvidenceClassification classification,
        string kind,
        string mediaType,
        EvidenceBasis basis,
        Uri? sourceUri = null,
        string? description = null,
        CancellationToken cancellationToken = default)
        => StoreCaptureFileAsync(
            projectId,
            captureId,
            sourceFile,
            classification,
            kind,
            mediaType,
            basis,
            sourceUri,
            description,
            captureRelativePath: null,
            cancellationToken);

    public async Task<StoredEvidenceArtifact> StoreCaptureFileAsync(
        Guid projectId,
        string captureId,
        string sourceFile,
        EvidenceClassification classification,
        string kind,
        string mediaType,
        EvidenceBasis basis,
        Uri? sourceUri,
        string? description,
        string? captureRelativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (!string.IsNullOrWhiteSpace(captureRelativePath))
        {
            ValidateCaptureRelativePath(captureRelativePath);
        }

        var source = Path.GetFullPath(sourceFile);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Evidence source file was not found.", source);
        }

        var hash = await ComputeSha256Async(source, cancellationToken);
        var info = new FileInfo(source);
        var extension = SafeExtension(info.Extension);
        var bucket = ClassificationFolder(classification);
        var relative = Path.Combine(bucket, hash[..2], hash + extension).Replace('\\', '/');
        var destination = Path.Combine(
            _paths.ProjectEvidenceRoot(projectId),
            relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindExistingAsync(
                projectId,
                captureId,
                classification,
                kind,
                hash,
                captureRelativePath,
                cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            if (!File.Exists(destination))
            {
                var temp = destination + ".partial-" + Guid.NewGuid().ToString("N");
                try
                {
                    await CopyFileAsync(source, temp, cancellationToken);
                    var copiedHash = await ComputeSha256Async(temp, cancellationToken);
                    if (!string.Equals(copiedHash, hash, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Evidence changed while being copied to the content-addressed store.");
                    }
                    File.Move(temp, destination, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
            }

            var retainFullMetadata = _metadataRetention == EvidenceMetadataRetention.Full;
            var artifact = new StoredEvidenceArtifact(
                EvidenceId: "EV-" + hash[..16].ToUpperInvariant(),
                ProjectId: projectId,
                CaptureId: captureId,
                Classification: classification,
                Kind: kind,
                OriginalName: retainFullMetadata ? info.Name : "evidence" + extension,
                RelativePath: "evidence/" + relative,
                Sha256: hash,
                SizeBytes: info.Length,
                MediaType: mediaType,
                StoredAt: DateTimeOffset.UtcNow,
                Basis: basis,
                SourceUri: retainFullMetadata ? sourceUri?.AbsoluteUri : null,
                Description: retainFullMetadata ? description : null,
                CaptureRelativePath: captureRelativePath);

            await AppendIndexAsync(projectId, artifact, cancellationToken);
            return artifact;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> VerifyAsync(StoredEvidenceArtifact artifact, CancellationToken cancellationToken = default)
    {
        var projectRoot = _paths.ProjectRoot(artifact.ProjectId);
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(projectRoot, fullPath) || !File.Exists(fullPath))
        {
            return false;
        }

        var hash = await ComputeSha256Async(fullPath, cancellationToken);
        return string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<StoredEvidenceArtifact>> ReadIndexAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var indexPath = IndexPath(projectId);
        if (!File.Exists(indexPath))
        {
            return [];
        }

        var result = new List<StoredEvidenceArtifact>();
        using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var artifact = JsonSerializer.Deserialize<StoredEvidenceArtifact>(line, JsonOptions);
            if (artifact is not null)
            {
                result.Add(artifact);
            }
        }

        return result;
    }

    private async Task<StoredEvidenceArtifact?> FindExistingAsync(
        Guid projectId,
        string captureId,
        EvidenceClassification classification,
        string kind,
        string sha256,
        string? captureRelativePath,
        CancellationToken cancellationToken)
    {
        var indexPath = IndexPath(projectId);
        if (!File.Exists(indexPath))
        {
            return null;
        }

        using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var artifact = JsonSerializer.Deserialize<StoredEvidenceArtifact>(line, JsonOptions);
            if (artifact is not null
                && artifact.ProjectId == projectId
                && string.Equals(artifact.CaptureId, captureId, StringComparison.Ordinal)
                && artifact.Classification == classification
                && string.Equals(artifact.Kind, kind, StringComparison.Ordinal)
                && string.Equals(artifact.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(artifact.CaptureRelativePath, captureRelativePath, StringComparison.Ordinal))
            {
                return artifact;
            }
        }

        return null;
    }

    private static void ValidateCaptureRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(relativePath)
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
        {
            throw new ArgumentException("Capture-relative evidence path must remain contained.", nameof(relativePath));
        }
    }

    private async Task AppendIndexAsync(Guid projectId, StoredEvidenceArtifact artifact, CancellationToken cancellationToken)
    {
        var index = IndexPath(projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        var json = JsonSerializer.Serialize(artifact, JsonOptions);
        await File.AppendAllTextAsync(index, json + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
    }

    private string IndexPath(Guid projectId) => Path.Combine(_paths.ProjectEvidenceRoot(projectId), "index.ndjson");

    private static string ClassificationFolder(EvidenceClassification classification) => classification switch
    {
        EvidenceClassification.Quarantine => "quarantine",
        EvidenceClassification.Sanitized => "sanitized",
        EvidenceClassification.NeutralKnowledge => "knowledge",
        _ => throw new ArgumentOutOfRangeException(nameof(classification))
    };

    private static string SafeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16)
        {
            return ".bin";
        }

        return extension.All(ch => ch == '.' || char.IsAsciiLetterOrDigit(ch))
            ? extension.ToLowerInvariant()
            : ".bin";
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}
