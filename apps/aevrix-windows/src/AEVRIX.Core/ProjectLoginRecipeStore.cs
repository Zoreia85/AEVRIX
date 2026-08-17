using System.Text;
using System.Text.Json;

namespace Aevrix.Core;

public sealed record ProjectLoginRecipeDescriptor(
    Guid ProjectId,
    string CanonicalLoginUri,
    LoginRecipe Recipe,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Stores non-secret login-form recipes inside the owning project. Credentials remain exclusively in
/// ProjectCredentialVault; this store contains only selectors and authenticated/logged-out markers.
/// </summary>
public sealed class ProjectLoginRecipeStore
{
    private const int SchemaVersion = 1;
    private const int MaxRecipesPerProject = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    private readonly AevrixDataPaths _paths;
    private readonly ProjectRepository _projects;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectLoginRecipeStore(
        AevrixDataPaths paths,
        ProjectRepository projects,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProjectLoginRecipeDescriptor> UpsertAsync(
        Guid projectId,
        LoginRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(projectId));
        }
        ArgumentNullException.ThrowIfNull(recipe);

        var envelope = await _projects.LoadAsync(projectId, cancellationToken);
        var normalized = ValidateAndNormalize(envelope, recipe);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadUnsafeAsync(projectId, cancellationToken);
            var entries = registry.Entries
                .Where(item => !string.Equals(item.CanonicalLoginUri, normalized.CanonicalLoginUri, StringComparison.Ordinal))
                .ToList();
            entries.Add(normalized);
            if (entries.Count > MaxRecipesPerProject)
            {
                throw new InvalidOperationException("Project login recipe limit exceeded.");
            }

            var document = new RegistryDocument(SchemaVersion, projectId, entries);
            await SaveUnsafeAsync(document, cancellationToken);
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProjectLoginRecipeDescriptor>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        _ = await _projects.LoadAsync(projectId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadUnsafeAsync(projectId, cancellationToken);
            return registry.Entries
                .OrderBy(item => item.CanonicalLoginUri, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectLoginRecipeDescriptor?> ResolveAsync(
        Guid projectId,
        Uri loginUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loginUri);
        _ = await _projects.LoadAsync(projectId, cancellationToken);
        var canonical = ProjectCredentialVault.CanonicalizeLoginUri(loginUri);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadUnsafeAsync(projectId, cancellationToken);
            return registry.Entries.SingleOrDefault(item =>
                string.Equals(item.CanonicalLoginUri, canonical, StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        Guid projectId,
        Uri loginUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loginUri);
        _ = await _projects.LoadAsync(projectId, cancellationToken);
        var canonical = ProjectCredentialVault.CanonicalizeLoginUri(loginUri);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadUnsafeAsync(projectId, cancellationToken);
            var remaining = registry.Entries
                .Where(item => !string.Equals(item.CanonicalLoginUri, canonical, StringComparison.Ordinal))
                .ToArray();
            if (remaining.Length == registry.Entries.Count)
            {
                return;
            }
            await SaveUnsafeAsync(new RegistryDocument(SchemaVersion, projectId, remaining), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ProjectLoginRecipeDescriptor ValidateAndNormalize(ProjectEnvelope envelope, LoginRecipe recipe)
    {
        if (envelope.Project.Domain != ProjectDomain.Web
            || envelope.Project.EntryPoint is null
            || envelope.BrowserPolicy is null)
        {
            throw new InvalidOperationException("Login recipes require a Web project with an active browser policy.");
        }

        recipe.Validate();
        if (!string.Equals(recipe.TargetId, envelope.Project.TargetId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Login recipe target does not match the owning project.");
        }

        var navigation = ResearchBrowserNavigationGate.Evaluate(envelope.BrowserPolicy, recipe.LoginUri);
        if (!navigation.Allowed)
        {
            throw new InvalidOperationException($"Login recipe URI is outside project browser policy: {navigation.Code}.");
        }

        ValidateSelector(recipe.UsernameSelector, nameof(recipe.UsernameSelector));
        ValidateSelector(recipe.PasswordSelector, nameof(recipe.PasswordSelector));
        ValidateSelector(recipe.SubmitSelector, nameof(recipe.SubmitSelector));
        ValidateMarkers(recipe.AuthenticatedUrlMarkers, nameof(recipe.AuthenticatedUrlMarkers));
        ValidateMarkers(recipe.AuthenticatedTextMarkers, nameof(recipe.AuthenticatedTextMarkers));
        ValidateMarkers(recipe.LoggedOutUrlMarkers, nameof(recipe.LoggedOutUrlMarkers));
        ValidateMarkers(recipe.LoggedOutTextMarkers, nameof(recipe.LoggedOutTextMarkers));

        var canonical = ProjectCredentialVault.CanonicalizeLoginUri(recipe.LoginUri);
        var normalizedRecipe = recipe with
        {
            LoginUri = new Uri(canonical, UriKind.Absolute),
            LearnedAt = recipe.LearnedAt == default ? _timeProvider.GetUtcNow() : recipe.LearnedAt
        };
        return new ProjectLoginRecipeDescriptor(
            envelope.Project.Id,
            canonical,
            normalizedRecipe,
            _timeProvider.GetUtcNow());
    }

    private async Task<RegistryDocument> LoadUnsafeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var path = RegistryPath(projectId);
        if (!File.Exists(path))
        {
            return new RegistryDocument(SchemaVersion, projectId, Array.Empty<ProjectLoginRecipeDescriptor>());
        }

        EnsureNotReparsePoint(path, "Project login recipe registry");
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<RegistryDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Project login recipe registry is empty or invalid.");
        ValidateRegistry(document, projectId);
        return document;
    }

    private async Task SaveUnsafeAsync(RegistryDocument document, CancellationToken cancellationToken)
    {
        ValidateRegistry(document, document.ProjectId);
        var root = BrowserMetadataRoot(document.ProjectId);
        Directory.CreateDirectory(root);
        EnsureNotReparsePoint(root, "Project browser metadata directory");

        var path = RegistryPath(document.ProjectId);
        if (File.Exists(path))
        {
            EnsureNotReparsePoint(path, "Project login recipe registry");
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temp,
                JsonSerializer.Serialize(document, JsonOptions),
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static void ValidateRegistry(RegistryDocument document, Guid expectedProjectId)
    {
        if (document.SchemaVersion != SchemaVersion || document.ProjectId != expectedProjectId)
        {
            throw new InvalidDataException("Project login recipe registry identity or schema is invalid.");
        }
        if (document.Entries.Count > MaxRecipesPerProject)
        {
            throw new InvalidDataException("Project login recipe registry exceeds its entry limit.");
        }

        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Entries)
        {
            if (item.ProjectId != expectedProjectId || !urls.Add(item.CanonicalLoginUri))
            {
                throw new InvalidDataException("Project login recipe registry contains duplicate or cross-project entries.");
            }
            var canonical = ProjectCredentialVault.CanonicalizeLoginUri(new Uri(item.CanonicalLoginUri, UriKind.Absolute));
            if (!string.Equals(canonical, item.CanonicalLoginUri, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Project login recipe registry contains a non-canonical URL.");
            }
            item.Recipe.Validate();
            if (!string.Equals(
                    ProjectCredentialVault.CanonicalizeLoginUri(item.Recipe.LoginUri),
                    item.CanonicalLoginUri,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Project login recipe payload and canonical URL disagree.");
            }
            ValidateSelector(item.Recipe.UsernameSelector, nameof(item.Recipe.UsernameSelector));
            ValidateSelector(item.Recipe.PasswordSelector, nameof(item.Recipe.PasswordSelector));
            ValidateSelector(item.Recipe.SubmitSelector, nameof(item.Recipe.SubmitSelector));
            ValidateMarkers(item.Recipe.AuthenticatedUrlMarkers, nameof(item.Recipe.AuthenticatedUrlMarkers));
            ValidateMarkers(item.Recipe.AuthenticatedTextMarkers, nameof(item.Recipe.AuthenticatedTextMarkers));
            ValidateMarkers(item.Recipe.LoggedOutUrlMarkers, nameof(item.Recipe.LoggedOutUrlMarkers));
            ValidateMarkers(item.Recipe.LoggedOutTextMarkers, nameof(item.Recipe.LoggedOutTextMarkers));
        }
    }

    private static void ValidateSelector(string selector, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector, parameterName);
        if (selector.Length > 512 || selector.Any(char.IsControl))
        {
            throw new ArgumentException("Login recipe selector is invalid.", parameterName);
        }
    }

    private static void ValidateMarkers(IReadOnlyList<string> markers, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(markers, parameterName);
        if (markers.Count > 32)
        {
            throw new ArgumentException("Login recipe marker count exceeds the limit.", parameterName);
        }
        foreach (var marker in markers)
        {
            if (string.IsNullOrWhiteSpace(marker) || marker.Length > 256 || marker.Any(char.IsControl))
            {
                throw new ArgumentException("Login recipe marker is invalid.", parameterName);
            }
        }
    }

    private string BrowserMetadataRoot(Guid projectId) =>
        Path.Combine(_paths.ProjectRoot(projectId), "browser");

    private string RegistryPath(Guid projectId) =>
        Path.Combine(BrowserMetadataRoot(projectId), "login-recipes.json");

    private static void EnsureNotReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} cannot be a reparse point.");
        }
    }

    private sealed record RegistryDocument(
        int SchemaVersion,
        Guid ProjectId,
        IReadOnlyList<ProjectLoginRecipeDescriptor> Entries);
}