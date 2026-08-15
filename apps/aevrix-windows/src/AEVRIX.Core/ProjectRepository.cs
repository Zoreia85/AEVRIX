using System.Text;
using System.Text.Json;

namespace Aevrix.Core;

public sealed record ProjectEnvelope(
    int SchemaVersion,
    CaptureProject Project,
    ResearchBrowserPolicy? BrowserPolicy,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string> Metadata)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed class ProjectRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AevrixDataPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectRepository(AevrixDataPaths paths)
    {
        _paths = paths.EnsureCreated();
    }

    public async Task<ProjectEnvelope> CreateAsync(
        CaptureProject project,
        ResearchBrowserPolicy? browserPolicy = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        browserPolicy?.Validate();

        var envelope = new ProjectEnvelope(
            ProjectEnvelope.CurrentSchemaVersion,
            project,
            browserPolicy,
            DateTimeOffset.UtcNow,
            metadata ?? new Dictionary<string, string>());

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = _paths.ProjectRoot(project.Id);
            if (Directory.Exists(root))
            {
                throw new IOException($"Project directory already exists: {project.Id:D}");
            }

            Directory.CreateDirectory(root);
            Directory.CreateDirectory(_paths.ProjectEvidenceRoot(project.Id));
            Directory.CreateDirectory(_paths.ProjectBlueprintRoot(project.Id));
            Directory.CreateDirectory(Path.Combine(root, "captures"));
            Directory.CreateDirectory(Path.Combine(root, "experiments"));
            Directory.CreateDirectory(Path.Combine(root, "logs"));

            await WriteEnvelopeAtomicAsync(envelope, cancellationToken);
            return envelope;
        }
        catch
        {
            var root = _paths.ProjectRoot(project.Id);
            if (Directory.Exists(root) && !File.Exists(_paths.ProjectManifest(project.Id)))
            {
                Directory.Delete(root, recursive: true);
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectEnvelope> SaveAsync(
        ProjectEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateEnvelope(envelope);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var updated = envelope with { UpdatedAt = DateTimeOffset.UtcNow };
            await WriteEnvelopeAtomicAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectEnvelope> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var path = _paths.ProjectManifest(projectId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("AEVRIX project manifest was not found.", path);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var envelope = await JsonSerializer.DeserializeAsync<ProjectEnvelope>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("AEVRIX project manifest is empty or invalid.");
        ValidateEnvelope(envelope);
        return envelope;
    }

    public async Task<IReadOnlyList<ProjectEnvelope>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ProjectEnvelope>();
        if (!Directory.Exists(_paths.ProjectsRoot))
        {
            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(_paths.ProjectsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(Path.GetFileName(directory), out var projectId))
            {
                continue;
            }

            try
            {
                result.Add(await LoadAsync(projectId, cancellationToken));
            }
            catch (InvalidDataException)
            {
                // Corrupt/incomplete project directories stay on disk for diagnostics but do not enter the normal list.
            }
        }

        return result.OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    private async Task WriteEnvelopeAtomicAsync(ProjectEnvelope envelope, CancellationToken cancellationToken)
    {
        ValidateEnvelope(envelope);
        var path = _paths.ProjectManifest(envelope.Project.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    private static void ValidateEnvelope(ProjectEnvelope envelope)
    {
        if (envelope.SchemaVersion != ProjectEnvelope.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported project schema version {envelope.SchemaVersion}.");
        }

        if (envelope.Project.Id == Guid.Empty)
        {
            throw new InvalidDataException("Project id cannot be empty.");
        }

        if (envelope.Project.Domain == ProjectDomain.Web)
        {
            if (envelope.Project.EntryPoint is null)
            {
                throw new InvalidDataException("Web project requires an entry point.");
            }
            envelope.BrowserPolicy?.Validate();
        }
        else if (string.IsNullOrWhiteSpace(envelope.Project.ArtifactPath))
        {
            throw new InvalidDataException("Binary/mobile project requires an artifact path.");
        }
    }
}
