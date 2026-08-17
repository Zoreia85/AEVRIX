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

    public string ProjectRoot(Guid projectId) => Path.Combine(ProjectsRoot, ValidateProjectId(projectId).ToString("D"));
    public string ProjectManifest(Guid projectId) => Path.Combine(ProjectRoot(projectId), "project.json");
    public string ProjectEvidenceRoot(Guid projectId) => Path.Combine(ProjectRoot(projectId), "evidence");
    public string ProjectBlueprintRoot(Guid projectId) => Path.Combine(ProjectRoot(projectId), "blueprint");

    /// <summary>
    /// Legacy target-only browser profile path. Persistent authenticated project sessions should use ProjectBrowserProfile.
    /// </summary>
    public string TargetBrowserProfile(string targetId) => Path.Combine(BrowserProfilesRoot, SafeTargetId(targetId));

    /// <summary>
    /// Browser profile isolated by both project and target so cookies, localStorage and authenticated state cannot be shared
    /// merely because two projects point at the same target.
    /// </summary>
    public string ProjectBrowserProfile(Guid projectId, string targetId) =>
        Path.Combine(BrowserProfilesRoot, ValidateProjectId(projectId).ToString("N"), SafeTargetId(targetId));

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

    private static Guid ValidateProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(projectId));
        }
        return projectId;
    }
}
