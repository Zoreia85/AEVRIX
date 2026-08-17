namespace Aevrix.Core;

public enum ProjectCurrentPageLoginStatus
{
    BlockedByPolicy,
    NoActivePage,
    RecipeNotFound,
    CredentialNotFound,
    AccountSelectionRequired,
    Submitted
}

public sealed record ProjectCurrentPageLoginRequest(
    Guid ProjectId,
    bool ProjectExecutionAuthorized,
    bool CredentialAutofillAuthorized,
    bool IsAutomaticRelogin = false);

public sealed record ProjectCurrentPageLoginResult(
    ProjectCurrentPageLoginStatus Status,
    string Code,
    IReadOnlyList<ProjectCredentialDescriptor> Candidates)
{
    public static ProjectCurrentPageLoginResult Blocked(string code) =>
        new(ProjectCurrentPageLoginStatus.BlockedByPolicy, code, Array.Empty<ProjectCredentialDescriptor>());

    public static ProjectCurrentPageLoginResult NoActivePage() =>
        new(ProjectCurrentPageLoginStatus.NoActivePage, "browser_has_no_active_page", Array.Empty<ProjectCredentialDescriptor>());

    public static ProjectCurrentPageLoginResult RecipeNotFound() =>
        new(ProjectCurrentPageLoginStatus.RecipeNotFound, "login_recipe_not_found_for_current_page", Array.Empty<ProjectCredentialDescriptor>());

    public static ProjectCurrentPageLoginResult FromAutomation(ProjectLoginAutomationResult automation) =>
        automation.Status switch
        {
            ProjectLoginAutomationStatus.BlockedByPolicy => Blocked(automation.Code),
            ProjectLoginAutomationStatus.CredentialNotFound => new(
                ProjectCurrentPageLoginStatus.CredentialNotFound,
                automation.Code,
                automation.Candidates),
            ProjectLoginAutomationStatus.AccountSelectionRequired => new(
                ProjectCurrentPageLoginStatus.AccountSelectionRequired,
                automation.Code,
                automation.Candidates),
            ProjectLoginAutomationStatus.Submitted => new(
                ProjectCurrentPageLoginStatus.Submitted,
                automation.Code,
                automation.Candidates),
            _ => throw new InvalidOperationException("Unsupported project login automation status.")
        };
}

/// <summary>
/// Resolves a persisted LoginRecipe from the browser's current page and delegates secret-aware execution to
/// ProjectResearchBrowserLoginCoordinator. Nothing is read from the credential vault until project, page,
/// policy and recipe resolution all pass.
/// </summary>
public sealed class ProjectCurrentPageLoginService
{
    private readonly ProjectRepository _projects;
    private readonly ProjectLoginRecipeStore _recipes;
    private readonly ProjectResearchBrowserLoginCoordinator _coordinator;

    public ProjectCurrentPageLoginService(
        ProjectRepository projects,
        ProjectLoginRecipeStore recipes,
        ProjectResearchBrowserLoginCoordinator coordinator)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public async Task<ProjectCurrentPageLoginResult> ExecuteAsync(
        ProjectCurrentPageLoginRequest request,
        IResearchBrowserLoginFormAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(adapter);
        if (request.ProjectId == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(request));
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.ProjectExecutionAuthorized || !request.CredentialAutofillAuthorized)
        {
            return ProjectCurrentPageLoginResult.Blocked("project_login_not_authorized");
        }

        if (adapter.CurrentUri is not Uri currentUri || !currentUri.IsAbsoluteUri)
        {
            return ProjectCurrentPageLoginResult.NoActivePage();
        }

        var envelope = await _projects.LoadAsync(request.ProjectId, cancellationToken);
        if (envelope.Project.Domain != ProjectDomain.Web
            || envelope.Project.EntryPoint is null
            || envelope.BrowserPolicy is null)
        {
            return ProjectCurrentPageLoginResult.Blocked("project_not_governed_web_target");
        }

        ResearchBrowserPolicy policy;
        try
        {
            policy = envelope.BrowserPolicy.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ProjectCurrentPageLoginResult.Blocked("project_browser_policy_invalid");
        }

        if (!string.Equals(envelope.Project.TargetId, policy.TargetId, StringComparison.Ordinal))
        {
            return ProjectCurrentPageLoginResult.Blocked("project_browser_policy_target_mismatch");
        }

        var navigation = ResearchBrowserNavigationGate.Evaluate(policy, currentUri);
        if (!navigation.Allowed)
        {
            return ProjectCurrentPageLoginResult.Blocked(navigation.Code);
        }

        var recipe = await _recipes.ResolveAsync(
            request.ProjectId,
            currentUri,
            cancellationToken);
        if (recipe is null)
        {
            return ProjectCurrentPageLoginResult.RecipeNotFound();
        }

        var automation = await _coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(
                request.ProjectId,
                recipe.Recipe,
                policy,
                request.ProjectExecutionAuthorized,
                request.CredentialAutofillAuthorized,
                request.IsAutomaticRelogin),
            adapter,
            cancellationToken);

        return ProjectCurrentPageLoginResult.FromAutomation(automation);
    }
}
