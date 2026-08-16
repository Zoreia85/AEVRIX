using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevrix.Core;

namespace AEVRIX.Desktop;

internal sealed record DesktopBlueprintProject(Guid Id, string Name, string Status);

internal sealed record DesktopBlueprintCapture(
    Guid ProjectId,
    string CaptureId,
    DateTimeOffset LastModifiedAt);

internal sealed record DesktopBlueprintSnapshot(
    ProjectBlueprint Blueprint,
    string CaptureId,
    string BlueprintSha256,
    bool ManifestVerified,
    string Detail);

internal sealed record BlueprintManifestDocument(
    int SchemaVersion,
    Guid ProjectId,
    string ProjectName,
    string TargetId,
    string Domain,
    DateTimeOffset GeneratedAt,
    string BlueprintSha256,
    IReadOnlyList<string> Files);

/// <summary>
/// Read-only reader for already-exported Project Blueprints. It validates the export manifest,
/// SHA-256 and ProjectBlueprint invariants before any model is shown in the Desktop.
/// </summary>
internal sealed class DesktopBlueprintExplorerService
{
    private const long MaxBlueprintBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AevrixDataPaths _paths;
    private readonly ProjectRepository _projects;

    public DesktopBlueprintExplorerService(AevrixDataPaths? paths = null)
    {
        _paths = (paths ?? AevrixDataPaths.ForCurrentUser()).EnsureCreated();
        _projects = new ProjectRepository(_paths);
    }

    public async Task<IReadOnlyList<DesktopBlueprintProject>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var envelopes = await _projects.ListAsync(cancellationToken).ConfigureAwait(false);
        return envelopes
            .Select(envelope => new DesktopBlueprintProject(
                envelope.Project.Id,
                envelope.Project.Name,
                envelope.Project.Status.ToString()))
            .ToArray();
    }

    public async Task<IReadOnlyList<DesktopBlueprintCapture>> ListCapturesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (projectId == Guid.Empty)
        {
            return Array.Empty<DesktopBlueprintCapture>();
        }

        var known = await _projects.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!known.Any(item => item.Project.Id == projectId))
        {
            return Array.Empty<DesktopBlueprintCapture>();
        }

        var root = _paths.ProjectBlueprintRoot(projectId);
        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            return Array.Empty<DesktopBlueprintCapture>();
        }

        var captures = new List<DesktopBlueprintCapture>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(directory))
            {
                continue;
            }

            var captureId = Path.GetFileName(directory);
            if (!EngineProtocol.IsSafeCaptureId(captureId))
            {
                continue;
            }

            var blueprintPath = Path.Combine(directory, "00_MANIFEST", "project-blueprint.json");
            var manifestPath = Path.Combine(directory, "00_MANIFEST", "manifest.json");
            if (!File.Exists(blueprintPath)
                || !File.Exists(manifestPath)
                || IsReparsePoint(blueprintPath)
                || IsReparsePoint(manifestPath))
            {
                continue;
            }

            var lastModified = new[]
            {
                File.GetLastWriteTimeUtc(blueprintPath),
                File.GetLastWriteTimeUtc(manifestPath)
            }.Max();
            captures.Add(new DesktopBlueprintCapture(
                projectId,
                captureId,
                new DateTimeOffset(DateTime.SpecifyKind(lastModified, DateTimeKind.Utc))));
        }

        return captures
            .OrderByDescending(item => item.LastModifiedAt)
            .ThenBy(item => item.CaptureId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<DesktopBlueprintSnapshot> LoadAsync(
        Guid expectedProjectId,
        string captureId,
        CancellationToken cancellationToken = default)
    {
        if (expectedProjectId == Guid.Empty)
        {
            throw new InvalidDataException("Blueprint project id is empty.");
        }
        if (!EngineProtocol.IsSafeCaptureId(captureId))
        {
            throw new InvalidDataException("Blueprint capture id is invalid.");
        }

        var known = await _projects.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!known.Any(item => item.Project.Id == expectedProjectId))
        {
            throw new InvalidDataException("Blueprint project is not part of the current local project catalog.");
        }

        var captureRoot = Path.GetFullPath(Path.Combine(_paths.ProjectBlueprintRoot(expectedProjectId), captureId));
        EnsureContained(_paths.ProjectBlueprintRoot(expectedProjectId), captureRoot);
        RejectReparsePath(captureRoot);

        var manifestRoot = Path.Combine(captureRoot, "00_MANIFEST");
        var blueprintPath = Path.Combine(manifestRoot, "project-blueprint.json");
        var manifestPath = Path.Combine(manifestRoot, "manifest.json");
        EnsureContained(captureRoot, blueprintPath);
        EnsureContained(captureRoot, manifestPath);
        RejectReparsePath(manifestRoot);
        RejectReparsePath(blueprintPath);
        RejectReparsePath(manifestPath);

        var blueprintInfo = new FileInfo(blueprintPath);
        var manifestInfo = new FileInfo(manifestPath);
        if (!blueprintInfo.Exists || !manifestInfo.Exists)
        {
            throw new FileNotFoundException("Blueprint export manifest is incomplete.");
        }
        if (blueprintInfo.Length is <= 0 or > MaxBlueprintBytes
            || manifestInfo.Length is <= 0 or > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("Blueprint export exceeds the Desktop read-only size policy.");
        }

        var blueprintJson = await File.ReadAllTextAsync(blueprintPath, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(blueprintJson)))
            .ToLowerInvariant();

        var manifestJson = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<BlueprintManifestDocument>(manifestJson, JsonOptions)
            ?? throw new InvalidDataException("Blueprint manifest is invalid or empty.");
        if (manifest.SchemaVersion != 1
            || manifest.ProjectId != expectedProjectId
            || !string.Equals(manifest.BlueprintSha256, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Blueprint manifest identity or SHA-256 validation failed.");
        }

        var blueprint = JsonSerializer.Deserialize<ProjectBlueprint>(blueprintJson, JsonOptions)
            ?? throw new InvalidDataException("Project Blueprint is invalid or empty.");
        blueprint.Validate();
        if (blueprint.ProjectId != expectedProjectId
            || !string.Equals(blueprint.ProjectName, manifest.ProjectName, StringComparison.Ordinal)
            || !string.Equals(blueprint.TargetId, manifest.TargetId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Project Blueprint identity does not match its signed export manifest context.");
        }

        return new DesktopBlueprintSnapshot(
            blueprint,
            captureId,
            actualHash,
            true,
            $"Manifesto e SHA-256 verificados. Blueprint schema {blueprint.SchemaVersion} validado sem reexecutar síntese.");
    }

    private static void EnsureContained(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Blueprint path escaped its project root.");
        }
    }

    private static void RejectReparsePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("Blueprint path was not found.", path);
        }
        if (IsReparsePoint(path))
        {
            throw new InvalidDataException("Blueprint reparse points are not accepted by the Desktop reader.");
        }
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
