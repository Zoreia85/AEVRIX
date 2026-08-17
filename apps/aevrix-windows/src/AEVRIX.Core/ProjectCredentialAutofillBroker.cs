namespace Aevrix.Core;

public enum ProjectCredentialAutofillStatus
{
    BlockedByPolicy,
    NotFound,
    Ambiguous,
    Ready
}

public sealed record ProjectCredentialAutofillRequest(
    Guid ProjectId,
    Uri LoginUri,
    bool ProjectExecutionAuthorized,
    bool CredentialAutofillAuthorized);

public sealed record ProjectCredentialAutofillDecision(
    ProjectCredentialAutofillStatus Status,
    ProjectCredentialLease? Credential,
    IReadOnlyList<ProjectCredentialDescriptor> Candidates,
    string Code)
{
    public static ProjectCredentialAutofillDecision Blocked() =>
        new(ProjectCredentialAutofillStatus.BlockedByPolicy, null, Array.Empty<ProjectCredentialDescriptor>(), "credential_autofill_not_authorized");

    public static ProjectCredentialAutofillDecision NotFound() =>
        new(ProjectCredentialAutofillStatus.NotFound, null, Array.Empty<ProjectCredentialDescriptor>(), "credential_not_found_for_login_url");

    public static ProjectCredentialAutofillDecision Ambiguous(IReadOnlyList<ProjectCredentialDescriptor> candidates) =>
        new(ProjectCredentialAutofillStatus.Ambiguous, null, candidates, "credential_selection_required");

    public static ProjectCredentialAutofillDecision Ready(ProjectCredentialLease credential) =>
        new(ProjectCredentialAutofillStatus.Ready, credential, new[] { credential.Descriptor }, "credential_ready_for_authorized_login");
}

/// <summary>
/// Fail-closed bridge between an authorized project execution and project-scoped credential retrieval.
/// Browser/runtime callers must use this broker instead of reading the vault directly for automatic login.
/// </summary>
public sealed class ProjectCredentialAutofillBroker
{
    private readonly ProjectCredentialVault _vault;

    public ProjectCredentialAutofillBroker(ProjectCredentialVault vault)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    public async Task<ProjectCredentialAutofillDecision> PrepareAsync(
        ProjectCredentialAutofillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProjectId == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(request));
        }
        ArgumentNullException.ThrowIfNull(request.LoginUri);

        if (!request.ProjectExecutionAuthorized || !request.CredentialAutofillAuthorized)
        {
            return ProjectCredentialAutofillDecision.Blocked();
        }

        var resolution = await _vault.ResolveForLoginAsync(request.ProjectId, request.LoginUri, cancellationToken);
        return resolution.Status switch
        {
            ProjectCredentialResolutionStatus.NotFound => ProjectCredentialAutofillDecision.NotFound(),
            ProjectCredentialResolutionStatus.Ambiguous => ProjectCredentialAutofillDecision.Ambiguous(resolution.Candidates),
            ProjectCredentialResolutionStatus.Resolved when resolution.Credential is not null =>
                ProjectCredentialAutofillDecision.Ready(resolution.Credential),
            _ => throw new InvalidDataException("Project credential resolution returned an invalid state.")
        };
    }
}
