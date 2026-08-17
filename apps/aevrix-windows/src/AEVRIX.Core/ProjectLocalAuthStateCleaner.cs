namespace Aevrix.Core;

public sealed record ProjectLocalAuthCleanupResult(
    Guid ProjectId,
    int CredentialsRemoved,
    bool BrowserProfileRemoved);

/// <summary>
/// Removes local authentication material that belongs to one project only.
/// The cleaner never touches another project's Credential Manager entries or browser profile directory.
/// </summary>
public sealed class ProjectLocalAuthStateCleaner
{
    private readonly AevrixDataPaths _paths;
    private readonly ProjectCredentialVault _credentialVault;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectLocalAuthStateCleaner(
        AevrixDataPaths paths,
        ProjectCredentialVault credentialVault)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
    }

    public async Task<ProjectLocalAuthCleanupResult> PurgeAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(projectId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var browserRoot = ProjectBrowserRoot(projectId);
            PreflightBrowserTree(browserRoot);

            var credentials = await _credentialVault.ListAsync(projectId, cancellationToken);
            foreach (var credential in credentials)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _credentialVault.DeleteAsync(projectId, credential.CredentialId, cancellationToken);
            }

            var browserRemoved = false;
            if (Directory.Exists(browserRoot))
            {
                Directory.Delete(browserRoot, recursive: true);
                browserRemoved = true;
            }

            return new ProjectLocalAuthCleanupResult(
                projectId,
                credentials.Count,
                browserRemoved);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ProjectBrowserRoot(Guid projectId) =>
        Path.Combine(_paths.BrowserProfilesRoot, projectId.ToString("N"));

    private static void PreflightBrowserTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            var directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Project browser profile cleanup rejects reparse points.");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Project browser profile cleanup rejects reparse points.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }
}
