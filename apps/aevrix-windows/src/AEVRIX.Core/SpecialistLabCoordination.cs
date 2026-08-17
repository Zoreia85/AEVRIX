namespace Aevrix.Core;

public enum SpecialistLab
{
    WebOnline,
    DesktopOffline,
    Mobile
}

public enum TargetKind
{
    Unknown = 0,
    HttpsWebApplication,
    WindowsExecutable,
    WindowsInstaller,
    WindowsLibrary,
    JavaArchive,
    MacDiskImage,
    MacInstallerPackage,
    LinuxAppImage,
    LinuxPackage,
    AndroidApk,
    AndroidAppBundle,
    AndroidXapk,
    AppleIpa
}

public enum RoutingEvidenceStrength
{
    Unknown = 0,
    ExtensionHint,
    TransportVerified
}

public enum DelegatedLabAuthority
{
    CandidateEvidenceOnly
}

public sealed record TargetRoute(
    SpecialistLab? Lab,
    TargetKind Kind,
    RoutingEvidenceStrength EvidenceStrength,
    string NormalizedTarget,
    bool RequiresContentVerification,
    string Reason)
{
    public bool IsRoutable => Lab is not null && Kind is not TargetKind.Unknown;
}

/// <summary>
/// Performs fail-closed preflight routing into the three specialist AEVRIX labs.
/// Artifact extensions are routing hints only; they never prove file format, safety,
/// provenance, licence, or execution eligibility. The receiving lab must verify content
/// before parsing or executing the artifact.
/// </summary>
public static class TargetIntakeRouter
{
    private static readonly Dictionary<string, (TargetKind Kind, SpecialistLab Lab)> ArtifactRoutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".exe"] = (TargetKind.WindowsExecutable, SpecialistLab.DesktopOffline),
            [".msi"] = (TargetKind.WindowsInstaller, SpecialistLab.DesktopOffline),
            [".msix"] = (TargetKind.WindowsInstaller, SpecialistLab.DesktopOffline),
            [".appx"] = (TargetKind.WindowsInstaller, SpecialistLab.DesktopOffline),
            [".dll"] = (TargetKind.WindowsLibrary, SpecialistLab.DesktopOffline),
            [".jar"] = (TargetKind.JavaArchive, SpecialistLab.DesktopOffline),
            [".dmg"] = (TargetKind.MacDiskImage, SpecialistLab.DesktopOffline),
            [".pkg"] = (TargetKind.MacInstallerPackage, SpecialistLab.DesktopOffline),
            [".appimage"] = (TargetKind.LinuxAppImage, SpecialistLab.DesktopOffline),
            [".deb"] = (TargetKind.LinuxPackage, SpecialistLab.DesktopOffline),
            [".rpm"] = (TargetKind.LinuxPackage, SpecialistLab.DesktopOffline),
            [".apk"] = (TargetKind.AndroidApk, SpecialistLab.Mobile),
            [".aab"] = (TargetKind.AndroidAppBundle, SpecialistLab.Mobile),
            [".xapk"] = (TargetKind.AndroidXapk, SpecialistLab.Mobile),
            [".ipa"] = (TargetKind.AppleIpa, SpecialistLab.Mobile)
        };

    public static TargetRoute ClassifyWeb(Uri entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);

        if (!entryPoint.IsAbsoluteUri || entryPoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Online AEVRIX targets require an absolute HTTPS entry point.", nameof(entryPoint));
        }

        if (string.IsNullOrWhiteSpace(entryPoint.Host))
        {
            throw new ArgumentException("Online AEVRIX targets require a host.", nameof(entryPoint));
        }

        if (!string.IsNullOrEmpty(entryPoint.UserInfo))
        {
            throw new ArgumentException("Online AEVRIX targets must not embed credentials in the URL.", nameof(entryPoint));
        }

        return new TargetRoute(
            SpecialistLab.WebOnline,
            TargetKind.HttpsWebApplication,
            RoutingEvidenceStrength.TransportVerified,
            entryPoint.AbsoluteUri,
            RequiresContentVerification: true,
            "HTTPS transport identifies the Web/Online lab. Application identity and behaviour still require evidence.");
    }

    public static TargetRoute ClassifyArtifact(string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);

        var normalizedPath = Path.GetFullPath(artifactPath);
        var extension = Path.GetExtension(normalizedPath);

        if (string.IsNullOrWhiteSpace(extension) || !ArtifactRoutes.TryGetValue(extension, out var route))
        {
            return new TargetRoute(
                Lab: null,
                TargetKind.Unknown,
                RoutingEvidenceStrength.Unknown,
                normalizedPath,
                RequiresContentVerification: true,
                "Artifact type is not safely routable from its filename. No specialist may execute it until classification is resolved.");
        }

        return new TargetRoute(
            route.Lab,
            route.Kind,
            RoutingEvidenceStrength.ExtensionHint,
            normalizedPath,
            RequiresContentVerification: true,
            $"The {extension.ToLowerInvariant()} suffix is only a routing hint. The receiving lab must verify magic/structure before use.");
    }
}

/// <summary>
/// Immutable request for a specialist lab to assist another lab without taking ownership
/// of the project or gaining authority to promote Trusted knowledge or canonical blueprints.
/// Delegated work can return candidate evidence only; Evidence Fusion/Judge remain central.
/// </summary>
public sealed record CrossLabHandoffRequest(
    Guid ProjectId,
    string TargetId,
    SpecialistLab OwningLab,
    SpecialistLab DelegatedLab,
    string WorkPackage,
    IReadOnlyList<string> EvidenceIds,
    DelegatedLabAuthority Authority)
{
    public static CrossLabHandoffRequest Create(
        Guid projectId,
        string targetId,
        SpecialistLab owningLab,
        SpecialistLab delegatedLab,
        string workPackage,
        IEnumerable<string>? evidenceIds = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A cross-lab handoff requires a project id.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workPackage);

        if (owningLab == delegatedLab)
        {
            throw new ArgumentException("A cross-lab handoff must delegate to a different specialist lab.", nameof(delegatedLab));
        }

        var normalizedEvidenceIds = (evidenceIds ?? Array.Empty<string>())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        return new CrossLabHandoffRequest(
            projectId,
            targetId.Trim().ToLowerInvariant(),
            owningLab,
            delegatedLab,
            workPackage.Trim(),
            normalizedEvidenceIds,
            DelegatedLabAuthority.CandidateEvidenceOnly);
    }
}
