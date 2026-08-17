namespace Aevrix.Core;

public enum ProjectLoginRecipeLearningStatus
{
    BlockedByPolicy,
    NotFound,
    Ambiguous,
    Rejected,
    Persisted
}

public sealed record ProjectLoginRecipeLearningRequest(
    Guid ProjectId,
    LoginFormSnapshot Snapshot,
    bool LearningAuthorized);

public sealed record ProjectLoginRecipeLearningResult(
    ProjectLoginRecipeLearningStatus Status,
    string Code,
    IReadOnlyList<string> CandidateSelectors,
    ProjectLoginRecipeDescriptor? PersistedRecipe)
{
    public static ProjectLoginRecipeLearningResult Blocked(string code) =>
        new(ProjectLoginRecipeLearningStatus.BlockedByPolicy, code, Array.Empty<string>(), null);

    public static ProjectLoginRecipeLearningResult FromDiscovery(LoginFormDiscoveryResult discovery) =>
        discovery.Status switch
        {
            LoginFormDiscoveryStatus.NotFound => new(
                ProjectLoginRecipeLearningStatus.NotFound,
                discovery.Code,
                discovery.CandidateSelectors,
                null),
            LoginFormDiscoveryStatus.Ambiguous => new(
                ProjectLoginRecipeLearningStatus.Ambiguous,
                discovery.Code,
                discovery.CandidateSelectors,
                null),
            LoginFormDiscoveryStatus.Rejected => new(
                ProjectLoginRecipeLearningStatus.Rejected,
                discovery.Code,
                discovery.CandidateSelectors,
                null),
            _ => throw new InvalidOperationException("Ready discovery requires persistence before a learning result can be returned.")
        };

    public static ProjectLoginRecipeLearningResult Persisted(ProjectLoginRecipeDescriptor descriptor) =>
        new(
            ProjectLoginRecipeLearningStatus.Persisted,
            "login_recipe_persisted",
            Array.Empty<string>(),
            descriptor);
}

/// <summary>
/// Explicit governance boundary between observing a page and teaching AEVRIX how to log in.
/// A DOM snapshot may be evaluated freely by the pure judge, but project state is modified only when
/// learning is explicitly authorized and discovery is uniquely Ready. Ambiguous/NotFound/Rejected states
/// never create or update a persisted recipe.
/// </summary>
public sealed class ProjectLoginRecipeLearningService
{
    private readonly ProjectRepository _projects;
    private readonly ProjectLoginRecipeStore _recipes;

    public ProjectLoginRecipeLearningService(
        ProjectRepository projects,
        ProjectLoginRecipeStore recipes)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
    }

    public async Task<ProjectLoginRecipeLearningResult> LearnAsync(
        ProjectLoginRecipeLearningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProjectId == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(request));
        }
        ArgumentNullException.ThrowIfNull(request.Snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.LearningAuthorized)
        {
            return ProjectLoginRecipeLearningResult.Blocked("login_recipe_learning_not_authorized");
        }

        var envelope = await _projects.LoadAsync(request.ProjectId, cancellationToken);
        if (envelope.Project.Domain != ProjectDomain.Web
            || envelope.Project.EntryPoint is null
            || envelope.BrowserPolicy is null)
        {
            return ProjectLoginRecipeLearningResult.Blocked("project_not_governed_web_target");
        }

        ResearchBrowserPolicy policy;
        try
        {
            policy = envelope.BrowserPolicy.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ProjectLoginRecipeLearningResult.Blocked("project_browser_policy_invalid");
        }

        if (!string.Equals(policy.TargetId, envelope.Project.TargetId, StringComparison.Ordinal))
        {
            return ProjectLoginRecipeLearningResult.Blocked("project_browser_policy_target_mismatch");
        }

        var discovery = LoginFormDiscoveryJudge.Evaluate(
            envelope.Project.TargetId,
            policy,
            request.Snapshot);

        if (discovery.Status != LoginFormDiscoveryStatus.Ready || discovery.Recipe is null)
        {
            return ProjectLoginRecipeLearningResult.FromDiscovery(discovery);
        }

        // ProjectLoginRecipeStore loads the current project again and revalidates target/host/policy before
        // writing. That second validation intentionally closes the race between observation and persistence.
        var persisted = await _recipes.UpsertAsync(
            request.ProjectId,
            discovery.Recipe,
            cancellationToken);
        return ProjectLoginRecipeLearningResult.Persisted(persisted);
    }
}