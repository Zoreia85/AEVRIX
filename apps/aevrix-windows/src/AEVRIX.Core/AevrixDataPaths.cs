namespace Aevrix.Core;

public sealed record AevrixDataPaths(
    string UserRoot,
    string ProjectsRoot,
    string VaultRoot,
    string BrowserProfilesRoot,
    string EngineRoot,
    string UpdatesRoot,
    string LogsRoot,
    string CacheRoot)
{
    public static AevrixDataPaths ForCurrentUser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "AEVRIX");
        return new AevrixDataPaths(
            UserRoot: root,
            ProjectsRoot: Path.Combine(root, "Projects"),
            VaultRoot: Path.Combine(root, "Vault"),
            BrowserProfilesRoot: Path.Combine(root, "BrowserProfiles"),
            EngineRoot: Path.Combine(root, "Engine"),
            UpdatesRoot: Path.Combine(root, "Updates"),
            LogsRoot: Path.Combine(root, "Logs"),
            CacheRoot: Path.Combine(root, "Cache"));
    }

    public AevrixDataPaths EnsureCreated()
    {
        foreach (var path in new[]
        {
            UserRoot,
            ProjectsRoot,
            VaultRoot,
            BrowserProfilesRoot,
            EngineRoot,
            UpdatesRoot,
            LogsRoot,
            CacheRoot
        })
        {
            Directory.CreateDirectory(path);
        }

        return this;
    }

    public string ProjectRoot(Guid projectId) => Path.Combine(ProjectsRoot, projectId.ToString("D"));
    public string ProjectManifest(Guid projectId) => Path.Combine(ProjectRoot(projectId), "project.json");
    public string ProjectEvidenceRoot(Guid projectId) => Path.Combine(ProjectRoot(projectId), "evidence");
    public string ProjectBlueprintRoot(Guid projectId) => Path.Combine(ProjectRoot(projectId), "blueprint");
    public string TargetBrowserProfile(string targetId) => Path.Combine(BrowserProfilesRoot, SafeTargetId(targetId));

    public static string SafeTargetId(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var normalized = targetId.Trim().ToLowerInvariant();
        if (normalized.Length is < 2 or > 64 || normalized.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
        {
            throw new ArgumentException("Target id contains unsupported characters.", nameof(targetId));
        }
        return normalized;
    }
}
