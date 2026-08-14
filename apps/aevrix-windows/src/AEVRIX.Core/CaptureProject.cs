namespace Aevrix.Core;

public enum ProjectDomain
{
    Web,
    WindowsBinary,
    AndroidMobile
}

public enum ProjectStatus
{
    Draft,
    Ready,
    Running,
    Paused,
    Partial,
    Complete,
    Blocked,
    Failed
}

public sealed record CaptureProject(
    Guid Id,
    string Name,
    string TargetId,
    ProjectDomain Domain,
    Uri? EntryPoint,
    string? ArtifactPath,
    ProjectStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastActivityAt,
    string? ActiveCaptureId,
    long SanitizedBytes,
    long QuarantineBytes)
{
    public static CaptureProject CreateWeb(string name, string targetId, Uri entryPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(entryPoint);

        if (entryPoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Online AEVRIX web projects require HTTPS.", nameof(entryPoint));
        }

        if (!string.IsNullOrEmpty(entryPoint.UserInfo))
        {
            throw new ArgumentException("Web project entry points must not embed credentials.", nameof(entryPoint));
        }

        return Create(name, targetId, ProjectDomain.Web, entryPoint, null);
    }

    public static CaptureProject CreateArtifact(string name, string targetId, ProjectDomain domain, string artifactPath)
    {
        if (domain is ProjectDomain.Web)
        {
            throw new ArgumentException("Use CreateWeb for a web project.", nameof(domain));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        return Create(name, targetId, domain, null, Path.GetFullPath(artifactPath));
    }

    private static CaptureProject Create(
        string name,
        string targetId,
        ProjectDomain domain,
        Uri? entryPoint,
        string? artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        return new CaptureProject(
            Guid.NewGuid(),
            name.Trim(),
            targetId.Trim().ToLowerInvariant(),
            domain,
            entryPoint,
            artifactPath,
            ProjectStatus.Draft,
            DateTimeOffset.UtcNow,
            null,
            null,
            0,
            0);
    }
}
