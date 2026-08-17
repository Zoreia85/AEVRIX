using System.Security.Cryptography;
using System.Text.Json;

namespace Aevrix.Core;

public enum ProjectCredentialResolutionStatus
{
    NotFound,
    Resolved,
    Ambiguous
}

public sealed record ProjectCredentialDescriptor(
    Guid CredentialId,
    Guid ProjectId,
    string Label,
    string CanonicalLoginUri,
    bool IsDefaultForLoginUri,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastUsedAtUtc);

public sealed record ProjectCredentialSecret(string UserName, string Password)
{
    public ProjectCredentialSecret Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Password);
        if (UserName.Length > 320)
        {
            throw new ArgumentOutOfRangeException(nameof(UserName), "Credential user name is too long.");
        }
        if (Password.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(Password), "Credential password is too long.");
        }
        return this;
    }
}

public interface IProjectCredentialSecretStore
{
    Task SaveAsync(Guid projectId, Guid credentialId, ProjectCredentialSecret secret, CancellationToken cancellationToken = default);
    Task<ProjectCredentialSecret?> ReadAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken = default);
}

public sealed class ProjectCredentialLease : IDisposable
{
    private char[]? _userName;
    private char[]? _password;

    internal ProjectCredentialLease(ProjectCredentialDescriptor descriptor, ProjectCredentialSecret secret)
    {
        Descriptor = descriptor;
        _userName = secret.UserName.ToCharArray();
        _password = secret.Password.ToCharArray();
    }

    public ProjectCredentialDescriptor Descriptor { get; }
    public ReadOnlyMemory<char> UserName => _userName ?? throw new ObjectDisposedException(nameof(ProjectCredentialLease));
    public ReadOnlyMemory<char> Password => _password ?? throw new ObjectDisposedException(nameof(ProjectCredentialLease));

    public void Dispose()
    {
        if (_userName is not null)
        {
            Array.Clear(_userName);
            _userName = null;
        }
        if (_password is not null)
        {
            Array.Clear(_password);
            _password = null;
        }
    }
}

public sealed record ProjectCredentialResolution(
    ProjectCredentialResolutionStatus Status,
    ProjectCredentialLease? Credential,
    IReadOnlyList<ProjectCredentialDescriptor> Candidates)
{
    public static ProjectCredentialResolution NotFound() =>
        new(ProjectCredentialResolutionStatus.NotFound, null, Array.Empty<ProjectCredentialDescriptor>());

    public static ProjectCredentialResolution Ambiguous(IReadOnlyList<ProjectCredentialDescriptor> candidates) =>
        new(ProjectCredentialResolutionStatus.Ambiguous, null, candidates);

    public static ProjectCredentialResolution Resolved(ProjectCredentialLease credential) =>
        new(ProjectCredentialResolutionStatus.Resolved, credential, new[] { credential.Descriptor });
}

public sealed class ProjectCredentialVault
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    private readonly string _registryRoot;
    private readonly IProjectCredentialSecretStore _secretStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectCredentialVault(
        AevrixDataPaths dataPaths,
        IProjectCredentialSecretStore secretStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataPaths);
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _registryRoot = Path.Combine(dataPaths.VaultRoot, "ProjectCredentials");
        Directory.CreateDirectory(_registryRoot);
    }

    public async Task<ProjectCredentialDescriptor> AddAsync(
        Guid projectId,
        string label,
        Uri loginUri,
        string userName,
        string password,
        bool makeDefaultForLoginUri = true,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        var normalizedLabel = ValidateLabel(label);
        var canonicalLoginUri = CanonicalizeLoginUri(loginUri);
        var secret = new ProjectCredentialSecret(userName, password).Validate();
        var credentialId = Guid.NewGuid();
        var descriptor = new ProjectCredentialDescriptor(
            credentialId,
            projectId,
            normalizedLabel,
            canonicalLoginUri,
            makeDefaultForLoginUri,
            _timeProvider.GetUtcNow(),
            null);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadRegistryUnsafeAsync(projectId, cancellationToken);
            var entries = registry.Entries.ToList();
            if (makeDefaultForLoginUri)
            {
                entries = entries
                    .Select(entry => string.Equals(entry.CanonicalLoginUri, canonicalLoginUri, StringComparison.Ordinal)
                        ? entry with { IsDefaultForLoginUri = false }
                        : entry)
                    .ToList();
            }

            await _secretStore.SaveAsync(projectId, credentialId, secret, cancellationToken);
            try
            {
                entries.Add(descriptor);
                await SaveRegistryUnsafeAsync(new RegistryDocument(SchemaVersion, projectId, entries), cancellationToken);
            }
            catch
            {
                await _secretStore.DeleteAsync(projectId, credentialId, CancellationToken.None);
                throw;
            }

            return descriptor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProjectCredentialDescriptor>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadRegistryUnsafeAsync(projectId, cancellationToken);
            return registry.Entries
                .OrderBy(entry => entry.CanonicalLoginUri, StringComparer.Ordinal)
                .ThenBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetDefaultAsync(
        Guid projectId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (credentialId == Guid.Empty)
        {
            throw new ArgumentException("Credential id must not be empty.", nameof(credentialId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadRegistryUnsafeAsync(projectId, cancellationToken);
            var selected = registry.Entries.SingleOrDefault(entry => entry.CredentialId == credentialId)
                ?? throw new KeyNotFoundException("Project credential was not found.");
            var entries = registry.Entries
                .Select(entry => string.Equals(entry.CanonicalLoginUri, selected.CanonicalLoginUri, StringComparison.Ordinal)
                    ? entry with { IsDefaultForLoginUri = entry.CredentialId == credentialId }
                    : entry)
                .ToArray();
            await SaveRegistryUnsafeAsync(new RegistryDocument(SchemaVersion, projectId, entries), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        Guid projectId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        if (credentialId == Guid.Empty)
        {
            throw new ArgumentException("Credential id must not be empty.", nameof(credentialId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadRegistryUnsafeAsync(projectId, cancellationToken);
            var remaining = registry.Entries.Where(entry => entry.CredentialId != credentialId).ToArray();
            if (remaining.Length == registry.Entries.Count)
            {
                return;
            }

            await _secretStore.DeleteAsync(projectId, credentialId, cancellationToken);
            await SaveRegistryUnsafeAsync(new RegistryDocument(SchemaVersion, projectId, remaining), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectCredentialResolution> ResolveForLoginAsync(
        Guid projectId,
        Uri currentUri,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        var canonicalLoginUri = CanonicalizeLoginUri(currentUri);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var registry = await LoadRegistryUnsafeAsync(projectId, cancellationToken);
            var candidates = registry.Entries
                .Where(entry => string.Equals(entry.CanonicalLoginUri, canonicalLoginUri, StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length == 0)
            {
                return ProjectCredentialResolution.NotFound();
            }

            ProjectCredentialDescriptor? selected;
            if (candidates.Length == 1)
            {
                selected = candidates[0];
            }
            else
            {
                var defaults = candidates.Where(entry => entry.IsDefaultForLoginUri).ToArray();
                if (defaults.Length != 1)
                {
                    return ProjectCredentialResolution.Ambiguous(candidates);
                }
                selected = defaults[0];
            }

            var secret = await _secretStore.ReadAsync(projectId, selected.CredentialId, cancellationToken);
            if (secret is null)
            {
                throw new InvalidDataException("Project credential metadata exists but its local secret is unavailable.");
            }
            secret.Validate();

            var usedAt = _timeProvider.GetUtcNow();
            var updated = registry.Entries
                .Select(entry => entry.CredentialId == selected.CredentialId
                    ? entry with { LastUsedAtUtc = usedAt }
                    : entry)
                .ToArray();
            await SaveRegistryUnsafeAsync(new RegistryDocument(SchemaVersion, projectId, updated), cancellationToken);
            var updatedSelected = selected with { LastUsedAtUtc = usedAt };
            return ProjectCredentialResolution.Resolved(new ProjectCredentialLease(updatedSelected, secret));
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string CanonicalizeLoginUri(Uri loginUri)
    {
        ArgumentNullException.ThrowIfNull(loginUri);
        if (!loginUri.IsAbsoluteUri || loginUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Project login URLs require absolute HTTPS.", nameof(loginUri));
        }
        if (!string.IsNullOrEmpty(loginUri.UserInfo))
        {
            throw new ArgumentException("Project login URLs must not embed credentials.", nameof(loginUri));
        }

        var builder = new UriBuilder(loginUri)
        {
            Scheme = Uri.UriSchemeHttps,
            Host = loginUri.IdnHost.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (loginUri.IsDefaultPort)
        {
            builder.Port = -1;
        }
        return builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    private async Task<RegistryDocument> LoadRegistryUnsafeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var path = RegistryPath(projectId);
        if (!File.Exists(path))
        {
            return new RegistryDocument(SchemaVersion, projectId, Array.Empty<ProjectCredentialDescriptor>());
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Project credential registry cannot be a reparse point.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<RegistryDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Project credential registry is empty or invalid.");
        ValidateRegistry(document, projectId);
        return document;
    }

    private async Task SaveRegistryUnsafeAsync(RegistryDocument registry, CancellationToken cancellationToken)
    {
        ValidateRegistry(registry, registry.ProjectId);
        Directory.CreateDirectory(_registryRoot);
        var path = RegistryPath(registry.ProjectId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, registry, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
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

    private static void ValidateRegistry(RegistryDocument registry, Guid expectedProjectId)
    {
        if (registry.SchemaVersion != SchemaVersion || registry.ProjectId != expectedProjectId)
        {
            throw new InvalidDataException("Project credential registry identity or schema is invalid.");
        }
        if (registry.Entries.Count > 256)
        {
            throw new InvalidDataException("Project credential registry exceeds the allowed entry count.");
        }

        var ids = new HashSet<Guid>();
        foreach (var entry in registry.Entries)
        {
            if (entry.CredentialId == Guid.Empty || entry.ProjectId != expectedProjectId || !ids.Add(entry.CredentialId))
            {
                throw new InvalidDataException("Project credential registry contains an invalid or duplicate credential identity.");
            }
            _ = ValidateLabel(entry.Label);
            var canonical = CanonicalizeLoginUri(new Uri(entry.CanonicalLoginUri, UriKind.Absolute));
            if (!string.Equals(canonical, entry.CanonicalLoginUri, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Project credential registry contains a non-canonical login URL.");
            }
        }

        foreach (var group in registry.Entries.GroupBy(entry => entry.CanonicalLoginUri, StringComparer.Ordinal))
        {
            if (group.Count(entry => entry.IsDefaultForLoginUri) > 1)
            {
                throw new InvalidDataException("A login URL cannot have more than one default project credential.");
            }
        }
    }

    private string RegistryPath(Guid projectId) =>
        Path.Combine(_registryRoot, projectId.ToString("N") + ".json");

    private static string ValidateLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var normalized = label.Trim();
        if (normalized.Length > 80 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Credential label is invalid.", nameof(label));
        }
        return normalized;
    }

    private static void ValidateProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(projectId));
        }
    }

    private sealed record RegistryDocument(
        int SchemaVersion,
        Guid ProjectId,
        IReadOnlyList<ProjectCredentialDescriptor> Entries);
}
