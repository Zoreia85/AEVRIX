using System.Text;
using System.Text.Json;

namespace Aevrix.Core;

public sealed record ProjectLoginRecipeDescriptor(
    Guid ProjectId,
    string CanonicalLoginUri,
    LoginRecipe Recipe,
    DateTimeOffset UpdatedAtUtc);

public sealed class ProjectLoginRecipeStore
{
    private const int SchemaVersion = 1;
    private const int MaxRecipesPerProject = 128;
    private const int MaxSelectorLength = 512;
    private const int MaxMarkersPerCollection = 32;
    private const int MaxMarkerLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    private readonly AevrixDataPaths _paths;
    private readonly ProjectRepository _projects;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectLoginRecipeStore(AevrixDataPaths paths, ProjectRepository projects, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProjectLoginRecipeDescriptor> UpsertAsync(Guid projectId, LoginRecipe recipe, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(recipe);
        var envelope = await _projects.LoadAsync(projectId, cancellationToken);
        var normalized = NormalizeAndValidate(envelope, recipe);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadUnsafeAsync(projectId, cancellationToken);
            ValidateRegistryForProject(registry, envelope);
            var entries = registry.Entries
                .Where(item => !string.Equals(item.CanonicalLoginUri, normalized.CanonicalLoginUri, StringComparison.Ordinal))
                .Append(normalized)
                .OrderBy(item => item.CanonicalLoginUri, StringComparer.Ordinal)
                .ToArray();
            if (entries.Length > MaxRecipesPerProject)
            {
                throw new InvalidOperationException("Project login recipe limit exceeded.");
            }
            await SaveUnsafeAsync(new RegistryDocument(SchemaVersion, projectId, entries), cancellationToken);
            return normalized;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ProjectLoginRecipeDescriptor>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        var envelope = await _projects.LoadAsync(projectId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadUnsafeAsync(projectId, cancellationToken);
            ValidateRegistryForProject(registry, envelope);
            return registry.Entries.OrderBy(item => item.CanonicalLoginUri, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ProjectLoginRecipeDescriptor?> ResolveAsync(Guid projectId, Uri loginUri, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(loginUri);
        var envelope = await _projects.LoadAsync(projectId, cancellationToken);
        var canonical = ProjectCredentialVault.CanonicalizeLoginUri(loginUri);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadUnsafeAsync(projectId, cancellationToken);
            ValidateRegistryForProject(registry, envelope);
            return registry.Entries.SingleOrDefault(item => string.Equals(item.CanonicalLoginUri, canonical, StringComparison.Ordinal));
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid projectId, Uri loginUri, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(loginUri);
        var envelope = await _projects.LoadAsync(projectId, cancellationToken);
        var canonical = ProjectCredentialVault.CanonicalizeLoginUri(loginUri);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadUnsafeAsync(projectId, cancellationToken);
            ValidateRegistryForProject(registry, envelope);
            var remaining = registry.Entries.Where(item => !string.Equals(item.CanonicalLoginUri, canonical, StringComparison.Ordinal)).ToArray();
            if (remaining.Length != registry.Entries.Count)
            {
                await SaveUnsafeAsync(new RegistryDocument(SchemaVersion, projectId, remaining), cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }

    private ProjectLoginRecipeDescriptor NormalizeAndValidate(ProjectEnvelope envelope, LoginRecipe recipe)
    {
        var policy = RequireGovernedWebProject(envelope);
        recipe.Validate();
        ValidateRecipeAgainstProject(envelope, policy, recipe);
        ValidateRecipePayload(recipe);
        var canonical = ProjectCredentialVault.CanonicalizeLoginUri(recipe.LoginUri);
        var normalized = recipe with
        {
            LoginUri = new Uri(canonical, UriKind.Absolute),
            LearnedAt = recipe.LearnedAt == default ? _timeProvider.GetUtcNow() : recipe.LearnedAt
        };
        return new ProjectLoginRecipeDescriptor(envelope.Project.Id, canonical, normalized, _timeProvider.GetUtcNow());
    }

    private async Task<RegistryDocument> LoadUnsafeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var path = RegistryPath(projectId);
        if (!File.Exists(path))
        {
            return new RegistryDocument(SchemaVersion, projectId, Array.Empty<ProjectLoginRecipeDescriptor>());
        }
        EnsureNotReparsePoint(path, "Project login recipe registry");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<RegistryDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Project login recipe registry is empty or invalid.");
        ValidateRegistryStructure(document, projectId);
        return document;
    }

    private async Task SaveUnsafeAsync(RegistryDocument document, CancellationToken cancellationToken)
    {
        ValidateRegistryStructure(document, document.ProjectId);
        var root = BrowserMetadataRoot(document.ProjectId);
        Directory.CreateDirectory(root);
        EnsureNotReparsePoint(root, "Project browser metadata directory");
        var path = RegistryPath(document.ProjectId);
        if (File.Exists(path)) EnsureNotReparsePoint(path, "Project login recipe registry");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void ValidateRegistryStructure(RegistryDocument document, Guid expectedProjectId)
    {
        if (document.SchemaVersion != SchemaVersion || document.ProjectId != expectedProjectId)
            throw new InvalidDataException("Project login recipe registry identity or schema is invalid.");
        if (document.Entries.Count > MaxRecipesPerProject)
            throw new InvalidDataException("Project login recipe registry exceeds its entry limit.");

        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Entries)
        {
            if (item.ProjectId != expectedProjectId || !urls.Add(item.CanonicalLoginUri))
                throw new InvalidDataException("Project login recipe registry contains duplicate or cross-project entries.");
            string canonical;
            try
            {
                canonical = ProjectCredentialVault.CanonicalizeLoginUri(new Uri(item.CanonicalLoginUri, UriKind.Absolute));
                item.Recipe.Validate();
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException("Project login recipe registry contains an invalid recipe.", ex);
            }
            if (!string.Equals(canonical, item.CanonicalLoginUri, StringComparison.Ordinal)
                || !string.Equals(ProjectCredentialVault.CanonicalizeLoginUri(item.Recipe.LoginUri), item.CanonicalLoginUri, StringComparison.Ordinal))
                throw new InvalidDataException("Project login recipe canonical URL is inconsistent.");
            ValidateRecipePayload(item.Recipe);
        }
    }

    private static void ValidateRegistryForProject(RegistryDocument document, ProjectEnvelope envelope)
    {
        ValidateRegistryStructure(document, envelope.Project.Id);
        var policy = RequireGovernedWebProject(envelope);
        foreach (var item in document.Entries) ValidateRecipeAgainstProject(envelope, policy, item.Recipe);
    }

    private static ResearchBrowserPolicy RequireGovernedWebProject(ProjectEnvelope envelope)
    {
        if (envelope.Project.Domain != ProjectDomain.Web || envelope.Project.EntryPoint is null || envelope.BrowserPolicy is null)
            throw new InvalidDataException("Login recipes require a Web project with an active browser policy.");
        if (!string.Equals(envelope.BrowserPolicy.TargetId, envelope.Project.TargetId, StringComparison.Ordinal))
            throw new InvalidDataException("Project browser policy target does not match project target.");
        envelope.BrowserPolicy.Validate();
        return envelope.BrowserPolicy;
    }

    private static void ValidateRecipeAgainstProject(ProjectEnvelope envelope, ResearchBrowserPolicy policy, LoginRecipe recipe)
    {
        if (!string.Equals(recipe.TargetId, envelope.Project.TargetId, StringComparison.Ordinal))
            throw new InvalidDataException("Login recipe target does not match the owning project.");
        var navigation = ResearchBrowserNavigationGate.Evaluate(policy, recipe.LoginUri);
        if (!navigation.Allowed)
            throw new InvalidDataException($"Login recipe URI is outside project browser policy: {navigation.Code}.");
    }

    private static void ValidateRecipePayload(LoginRecipe recipe)
    {
        ValidateSelector(recipe.UsernameSelector, nameof(recipe.UsernameSelector));
        ValidateSelector(recipe.PasswordSelector, nameof(recipe.PasswordSelector));
        ValidateSelector(recipe.SubmitSelector, nameof(recipe.SubmitSelector));
        ValidateMarkers(recipe.AuthenticatedUrlMarkers, nameof(recipe.AuthenticatedUrlMarkers));
        ValidateMarkers(recipe.AuthenticatedTextMarkers, nameof(recipe.AuthenticatedTextMarkers));
        ValidateMarkers(recipe.LoggedOutUrlMarkers, nameof(recipe.LoggedOutUrlMarkers));
        ValidateMarkers(recipe.LoggedOutTextMarkers, nameof(recipe.LoggedOutTextMarkers));
    }

    private static void ValidateSelector(string selector, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector, parameterName);
        if (selector.Length > MaxSelectorLength || selector.Any(char.IsControl)) throw new ArgumentException("Login recipe selector is invalid.", parameterName);
    }

    private static void ValidateMarkers(IReadOnlyList<string> markers, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(markers, parameterName);
        if (markers.Count > MaxMarkersPerCollection) throw new ArgumentException("Login recipe marker count exceeds the limit.", parameterName);
        foreach (var marker in markers)
        {
            if (string.IsNullOrWhiteSpace(marker) || marker.Length > MaxMarkerLength || marker.Any(char.IsControl))
                throw new ArgumentException("Login recipe marker is invalid.", parameterName);
        }
    }

    private string BrowserMetadataRoot(Guid projectId) => Path.Combine(_paths.ProjectRoot(projectId), "browser");
    private string RegistryPath(Guid projectId) => Path.Combine(BrowserMetadataRoot(projectId), "login-recipes.json");

    private static void EnsureNotReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"{description} cannot be a reparse point.");
    }

    private static void ValidateProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project id must not be empty.", nameof(projectId));
    }

    private sealed record RegistryDocument(int SchemaVersion, Guid ProjectId, IReadOnlyList<ProjectLoginRecipeDescriptor> Entries);
}