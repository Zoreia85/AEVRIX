namespace Aevrix.Core;

public enum ProjectLoginAutomationStatus
{
    BlockedByPolicy,
    CredentialNotFound,
    AccountSelectionRequired,
    Submitted
}

public sealed record ProjectLoginAutomationRequest(
    Guid ProjectId,
    LoginRecipe Recipe,
    ResearchBrowserPolicy Policy,
    bool ProjectExecutionAuthorized,
    bool CredentialAutofillAuthorized,
    bool IsAutomaticRelogin = false);

public sealed record ProjectLoginAutomationResult(
    ProjectLoginAutomationStatus Status,
    string Code,
    IReadOnlyList<ProjectCredentialDescriptor> Candidates)
{
    public static ProjectLoginAutomationResult Blocked(string code) =>
        new(ProjectLoginAutomationStatus.BlockedByPolicy, code, Array.Empty<ProjectCredentialDescriptor>());

    public static ProjectLoginAutomationResult NotFound() =>
        new(ProjectLoginAutomationStatus.CredentialNotFound, "credential_not_found_for_login_url", Array.Empty<ProjectCredentialDescriptor>());

    public static ProjectLoginAutomationResult SelectionRequired(IReadOnlyList<ProjectCredentialDescriptor> candidates) =>
        new(ProjectLoginAutomationStatus.AccountSelectionRequired, "credential_selection_required", candidates);

    public static ProjectLoginAutomationResult Submitted() =>
        new(ProjectLoginAutomationStatus.Submitted, "login_form_submitted", Array.Empty<ProjectCredentialDescriptor>());
}

/// <summary>
/// Minimal secret-aware browser adapter boundary. Implementations must not log or persist the supplied values.
/// </summary>
public interface IResearchBrowserLoginFormAdapter
{
    Uri? CurrentUri { get; }

    Task NavigateAsync(Uri loginUri, CancellationToken cancellationToken = default);

    Task FillAsync(
        string selector,
        ReadOnlyMemory<char> value,
        CancellationToken cancellationToken = default);

    Task SubmitAsync(string selector, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional hardened adapter boundary for browser hosts that can consume both credential fields in one
/// controlled operation. The coordinator prefers this contract when available, reducing secret lifetime
/// and avoiding separate host calls for username, password and submit.
/// </summary>
public interface IResearchBrowserAtomicLoginFormAdapter : IResearchBrowserLoginFormAdapter
{
    Task FillCredentialsAndSubmitAsync(
        LoginRecipe recipe,
        ReadOnlyMemory<char> userName,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates a project-scoped credential with a validated Research Browser login recipe.
/// The coordinator never exposes a secret unless project execution, autofill policy, target binding and host allowlist all pass.
/// </summary>
public sealed class ProjectResearchBrowserLoginCoordinator
{
    private readonly ProjectCredentialAutofillBroker _credentialBroker;

    public ProjectResearchBrowserLoginCoordinator(ProjectCredentialAutofillBroker credentialBroker)
    {
        _credentialBroker = credentialBroker ?? throw new ArgumentNullException(nameof(credentialBroker));
    }

    public async Task<ProjectLoginAutomationResult> ExecuteAsync(
        ProjectLoginAutomationRequest request,
        IResearchBrowserLoginFormAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(adapter);
        if (request.ProjectId == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(request));
        }

        var recipe = request.Recipe?.Validate()
            ?? throw new ArgumentException("A login recipe is required.", nameof(request));
        var policy = request.Policy?.Validate()
            ?? throw new ArgumentException("A Research Browser policy is required.", nameof(request));

        if (!request.ProjectExecutionAuthorized || !request.CredentialAutofillAuthorized)
        {
            return ProjectLoginAutomationResult.Blocked("project_login_not_authorized");
        }
        if (!policy.RememberCredentials)
        {
            return ProjectLoginAutomationResult.Blocked("credential_persistence_not_enabled");
        }
        if (request.IsAutomaticRelogin && !policy.AutomaticRelogin)
        {
            return ProjectLoginAutomationResult.Blocked("automatic_relogin_not_enabled");
        }
        if (!string.Equals(recipe.TargetId, policy.TargetId, StringComparison.Ordinal))
        {
            return ProjectLoginAutomationResult.Blocked("login_recipe_target_mismatch");
        }
        if (!policy.AllowedHosts.Contains(recipe.LoginUri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return ProjectLoginAutomationResult.Blocked("login_host_outside_allowlist");
        }

        var decision = await _credentialBroker.PrepareAsync(
            new ProjectCredentialAutofillRequest(
                request.ProjectId,
                recipe.LoginUri,
                ProjectExecutionAuthorized: true,
                CredentialAutofillAuthorized: true),
            cancellationToken);

        if (decision.Status == ProjectCredentialAutofillStatus.NotFound)
        {
            return ProjectLoginAutomationResult.NotFound();
        }
        if (decision.Status == ProjectCredentialAutofillStatus.Ambiguous)
        {
            return ProjectLoginAutomationResult.SelectionRequired(decision.Candidates);
        }
        if (decision.Status != ProjectCredentialAutofillStatus.Ready || decision.Credential is null)
        {
            return ProjectLoginAutomationResult.Blocked(decision.Code);
        }

        using var credential = decision.Credential;
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAtLoginUri(adapter.CurrentUri, recipe.LoginUri))
        {
            await adapter.NavigateAsync(recipe.LoginUri, cancellationToken);
        }

        if (adapter is IResearchBrowserAtomicLoginFormAdapter atomicAdapter)
        {
            await atomicAdapter.FillCredentialsAndSubmitAsync(
                recipe,
                credential.UserName,
                credential.Password,
                cancellationToken);
        }
        else
        {
            await adapter.FillAsync(recipe.UsernameSelector, credential.UserName, cancellationToken);
            await adapter.FillAsync(recipe.PasswordSelector, credential.Password, cancellationToken);
            await adapter.SubmitAsync(recipe.SubmitSelector, cancellationToken);
        }

        return ProjectLoginAutomationResult.Submitted();
    }

    private static bool IsAtLoginUri(Uri? currentUri, Uri loginUri)
    {
        if (currentUri is null || !currentUri.IsAbsoluteUri)
        {
            return false;
        }

        try
        {
            var current = ProjectCredentialVault.CanonicalizeLoginUri(currentUri);
            var login = ProjectCredentialVault.CanonicalizeLoginUri(loginUri);
            return string.Equals(current, login, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
