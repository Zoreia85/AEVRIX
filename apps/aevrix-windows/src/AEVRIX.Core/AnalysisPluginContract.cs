using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Core;

public enum TargetAccessClass
{
    Owned,
    ExplicitlyAuthorized,
    ThirdPartyCleanRoom
}

public enum AnalysisTechnique
{
    StaticInspection,
    AuthorizedRuntimeObservation,
    AuthorizedDynamicInstrumentation,
    AuthorizedMemoryInspection,
    AuthorizedNetworkCapture
}

public enum AnalysisEvidenceSensitivity
{
    Public,
    Internal,
    PersonalData,
    Restricted
}

public enum OutputBoundary
{
    LocalWorkspaceOnly,
    RedactedExternal
}

public sealed record WorkspaceScope(
    string WorkspaceId,
    string UserId,
    string EncryptionContextId)
{
    public void Validate()
    {
        ValidateToken(WorkspaceId, nameof(WorkspaceId));
        ValidateToken(UserId, nameof(UserId));
        ValidateToken(EncryptionContextId, nameof(EncryptionContextId));
    }

    internal static void ValidateToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 160 || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Scope identifiers must be compact non-whitespace tokens of at most 160 characters.", parameterName);
        }
    }
}

public sealed record AnalysisTarget(
    string TargetId,
    TargetAccessClass AccessClass,
    string Domain,
    string System,
    IReadOnlyCollection<string> Languages,
    IReadOnlyCollection<string> Formats)
{
    public void Validate()
    {
        WorkspaceScope.ValidateToken(TargetId, nameof(TargetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(Domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(System);
        ArgumentNullException.ThrowIfNull(Languages);
        ArgumentNullException.ThrowIfNull(Formats);

        if (Languages.Count == 0 && Formats.Count == 0)
        {
            throw new ArgumentException("A target must declare at least one language or format.");
        }

        if (Languages.Any(string.IsNullOrWhiteSpace) || Formats.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Target language and format identifiers cannot be blank.");
        }
    }
}

public sealed record AnalysisPluginDescriptor(
    string PluginId,
    string Version,
    IReadOnlyCollection<string> Domains,
    IReadOnlyCollection<string> Systems,
    IReadOnlyCollection<string> Languages,
    IReadOnlyCollection<string> Formats,
    IReadOnlyCollection<AnalysisTechnique> Techniques,
    bool RequiresNetwork,
    bool MayProcessPersonalData)
{
    public void Validate()
    {
        WorkspaceScope.ValidateToken(PluginId, nameof(PluginId));
        WorkspaceScope.ValidateToken(Version, nameof(Version));
        ValidateDimension(Domains, nameof(Domains));
        ValidateDimension(Systems, nameof(Systems));
        ValidateDimension(Languages, nameof(Languages));
        ValidateDimension(Formats, nameof(Formats));
        ArgumentNullException.ThrowIfNull(Techniques);
        if (Techniques.Count == 0)
        {
            throw new ArgumentException("A plugin must declare at least one analysis technique.", nameof(Techniques));
        }
    }

    public bool Supports(AnalysisTarget target, AnalysisTechnique technique)
    {
        target.Validate();
        Validate();
        return Techniques.Contains(technique)
            && Matches(Domains, target.Domain)
            && Matches(Systems, target.System)
            && target.Languages.All(value => Matches(Languages, value))
            && target.Formats.All(value => Matches(Formats, value));
    }

    private static void ValidateDimension(IReadOnlyCollection<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count == 0 || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Plugin capability dimensions must contain at least one non-blank value; use '*' for an explicit wildcard.", parameterName);
        }
    }

    private static bool Matches(IReadOnlyCollection<string> supported, string requested) =>
        supported.Contains("*", StringComparer.OrdinalIgnoreCase)
        || supported.Contains(requested, StringComparer.OrdinalIgnoreCase);
}

public sealed record AnalysisExecutionRequest(
    string RequestId,
    WorkspaceScope Scope,
    AnalysisTarget Target,
    AnalysisTechnique Technique,
    AnalysisEvidenceSensitivity Sensitivity,
    OutputBoundary OutputBoundary,
    bool AuthenticationOrAccessControlBypassRequested,
    bool LicenseOrDrmBypassRequested,
    bool CrossWorkspaceReadRequested)
{
    public void ValidateAgainst(AnalysisPluginDescriptor plugin)
    {
        WorkspaceScope.ValidateToken(RequestId, nameof(RequestId));
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(plugin);
        Scope.Validate();
        Target.Validate();
        plugin.Validate();

        if (!plugin.Supports(Target, Technique))
        {
            throw new InvalidOperationException($"Plugin '{plugin.PluginId}' does not support the requested target or technique.");
        }

        if (AuthenticationOrAccessControlBypassRequested || LicenseOrDrmBypassRequested)
        {
            throw new InvalidOperationException("AEVRIX does not authorize bypass of authentication, access controls, licensing, or DRM.");
        }

        if (CrossWorkspaceReadRequested)
        {
            throw new InvalidOperationException("Cross-workspace reads are forbidden by the execution contract.");
        }

        if (Target.AccessClass == TargetAccessClass.ThirdPartyCleanRoom
            && Technique is not (AnalysisTechnique.StaticInspection or AnalysisTechnique.AuthorizedRuntimeObservation))
        {
            throw new InvalidOperationException("Third-party clean-room targets are limited to static inspection and non-invasive authorized runtime observation.");
        }

        if (Sensitivity is AnalysisEvidenceSensitivity.PersonalData or AnalysisEvidenceSensitivity.Restricted)
        {
            if (OutputBoundary != OutputBoundary.LocalWorkspaceOnly)
            {
                throw new InvalidOperationException("Sensitive evidence must remain inside its local workspace boundary.");
            }

            if (!plugin.MayProcessPersonalData && Sensitivity == AnalysisEvidenceSensitivity.PersonalData)
            {
                throw new InvalidOperationException("The selected plugin is not declared for personal-data processing.");
            }
        }
    }
}

public sealed record EvidenceBlueprintBinding(
    string WorkspaceId,
    string EvidenceSha256,
    string BlueprintSha256,
    string PluginId,
    string RequestId)
{
    public string ComputeBindingSha256()
    {
        WorkspaceScope.ValidateToken(WorkspaceId, nameof(WorkspaceId));
        WorkspaceScope.ValidateToken(PluginId, nameof(PluginId));
        WorkspaceScope.ValidateToken(RequestId, nameof(RequestId));
        ValidateSha256(EvidenceSha256, nameof(EvidenceSha256));
        ValidateSha256(BlueprintSha256, nameof(BlueprintSha256));

        var canonical = string.Join('\n', new[]
        {
            "AEVRIX-EVIDENCE-BLUEPRINT-BINDING-V1",
            WorkspaceId,
            EvidenceSha256.ToLowerInvariant(),
            BlueprintSha256.ToLowerInvariant(),
            PluginId,
            RequestId
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new ArgumentException("Digest must be a 64-character hexadecimal SHA-256 value.", parameterName);
        }
    }
}
