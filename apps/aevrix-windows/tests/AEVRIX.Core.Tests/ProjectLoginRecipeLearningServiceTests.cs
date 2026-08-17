using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectLoginRecipeLearningServiceTests
{
    [TestMethod]
    public async Task LearnAsync_UnauthorizedObservationDoesNotCreateRecipeRegistry()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();

        var result = await fixture.Service.LearnAsync(new(
            project.Project.Id,
            ReadySnapshot(),
            LearningAuthorized: false));

        Assert.AreEqual(ProjectLoginRecipeLearningStatus.BlockedByPolicy, result.Status);
        Assert.AreEqual("login_recipe_learning_not_authorized", result.Code);
        Assert.IsNull(result.PersistedRecipe);
        Assert.IsFalse(File.Exists(fixture.RegistryPath(project.Project.Id)));
    }

    [TestMethod]
    public async Task LearnAsync_UniqueReadyFormPersistsCanonicalRecipe()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();

        var result = await fixture.Service.LearnAsync(new(
            project.Project.Id,
            ReadySnapshot(),
            LearningAuthorized: true));

        Assert.AreEqual(ProjectLoginRecipeLearningStatus.Persisted, result.Status);
        Assert.AreEqual("login_recipe_persisted", result.Code);
        Assert.IsNotNull(result.PersistedRecipe);
        Assert.AreEqual("https://example.com/login", result.PersistedRecipe.CanonicalLoginUri);
        Assert.AreEqual("#user", result.PersistedRecipe.Recipe.UsernameSelector);
        Assert.AreEqual("#secret", result.PersistedRecipe.Recipe.PasswordSelector);
        Assert.AreEqual("#submit", result.PersistedRecipe.Recipe.SubmitSelector);

        var stored = await fixture.Store.ListAsync(project.Project.Id);
        Assert.AreEqual(1, stored.Count);
    }

    [TestMethod]
    public async Task LearnAsync_AmbiguousPasswordFormNeverPersists()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        var snapshot = Snapshot(
            Element("#user", "input", "email", 0, autoComplete: "username"),
            Element("#first-secret", "input", "password", 1),
            Element("#second-secret", "input", "password", 2),
            Element("#submit", "button", "submit", 3));

        var result = await fixture.Service.LearnAsync(new(
            project.Project.Id,
            snapshot,
            LearningAuthorized: true));

        Assert.AreEqual(ProjectLoginRecipeLearningStatus.Ambiguous, result.Status);
        Assert.AreEqual("multiple_password_fields", result.Code);
        CollectionAssert.AreEquivalent(
            new[] { "#first-secret", "#second-secret" },
            result.CandidateSelectors.ToArray());
        Assert.IsFalse(File.Exists(fixture.RegistryPath(project.Project.Id)));
    }

    [TestMethod]
    public async Task LearnAsync_NotFoundFormNeverPersists()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        var snapshot = Snapshot(
            Element("#user", "input", "email", 0, autoComplete: "username"),
            Element("#submit", "button", "submit", 1));

        var result = await fixture.Service.LearnAsync(new(
            project.Project.Id,
            snapshot,
            LearningAuthorized: true));

        Assert.AreEqual(ProjectLoginRecipeLearningStatus.NotFound, result.Status);
        Assert.AreEqual("password_field_not_found", result.Code);
        Assert.IsFalse(File.Exists(fixture.RegistryPath(project.Project.Id)));
    }

    [TestMethod]
    public async Task LearnAsync_PageOutsideAllowlistIsRejectedWithoutPersistence()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        var snapshot = ReadySnapshot() with
        {
            PageUri = new Uri("https://other.example/login")
        };

        var result = await fixture.Service.LearnAsync(new(
            project.Project.Id,
            snapshot,
            LearningAuthorized: true));

        Assert.AreEqual(ProjectLoginRecipeLearningStatus.Rejected, result.Status);
        Assert.AreEqual("navigation_host_not_allowed", result.Code);
        Assert.IsFalse(File.Exists(fixture.RegistryPath(project.Project.Id)));
    }

    [TestMethod]
    public async Task LearnAsync_RelearningSameCanonicalPageUpdatesSingleRecipe()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        var first = ReadySnapshot();
        var second = ReadySnapshot() with
        {
            PageUri = new Uri("https://EXAMPLE.com:443/login?return=%2Fnew#form"),
            Elements = new[]
            {
                Element("#email-new", "input", "email", 0, autoComplete: "username"),
                Element("#secret-new", "input", "password", 1),
                Element("#submit-new", "button", "submit", 2)
            }
        };

        _ = await fixture.Service.LearnAsync(new(project.Project.Id, first, true));
        var result = await fixture.Service.LearnAsync(new(project.Project.Id, second, true));

        Assert.AreEqual(ProjectLoginRecipeLearningStatus.Persisted, result.Status);
        var stored = await fixture.Store.ListAsync(project.Project.Id);
        Assert.AreEqual(1, stored.Count);
        Assert.AreEqual("#email-new", stored[0].Recipe.UsernameSelector);
        Assert.AreEqual("https://example.com/login", stored[0].CanonicalLoginUri);
    }

    private static LoginFormSnapshot ReadySnapshot() => Snapshot(
        Element("#user", "input", "email", 0, autoComplete: "username"),
        Element("#secret", "input", "password", 1),
        Element("#submit", "button", "submit", 2, visibleText: "Sign in"));

    private static LoginFormSnapshot Snapshot(params LoginDomElement[] elements) => new(
        PageUri: new Uri("https://example.com/login?return=%2Fapp#form"),
        Elements: elements,
        ObservedAtUtc: DateTimeOffset.UtcNow);

    private static LoginDomElement Element(
        string selector,
        string tagName,
        string inputType,
        int order,
        string? autoComplete = null,
        string? visibleText = null) => new(
            Selector: selector,
            FormKey: "#login",
            TagName: tagName,
            InputType: inputType,
            Name: null,
            Id: null,
            AutoComplete: autoComplete,
            AriaLabel: null,
            Placeholder: null,
            VisibleText: visibleText,
            IsVisible: true,
            IsEnabled: true,
            DocumentOrder: order);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "aevrix-login-learning-tests",
                Guid.NewGuid().ToString("N"));
            Paths = new AevrixDataPaths(
                Root,
                Path.Combine(Root, "Projects"),
                Path.Combine(Root, "Vault"),
                Path.Combine(Root, "BrowserProfiles"),
                Path.Combine(Root, "Engine"),
                Path.Combine(Root, "Updates"),
                Path.Combine(Root, "Logs"),
                Path.Combine(Root, "Cache")).EnsureCreated();
            Projects = new ProjectRepository(Paths);
            Store = new ProjectLoginRecipeStore(Paths, Projects);
            Service = new ProjectLoginRecipeLearningService(Projects, Store);
        }

        public string Root { get; }
        public AevrixDataPaths Paths { get; }
        public ProjectRepository Projects { get; }
        public ProjectLoginRecipeStore Store { get; }
        public ProjectLoginRecipeLearningService Service { get; }

        public async Task<ProjectEnvelope> CreateProjectAsync()
        {
            var project = CaptureProject.CreateWeb(
                "Learning Project",
                "target-web",
                new Uri("https://example.com/"));
            var policy = new ResearchBrowserPolicy(
                TargetId: "target-web",
                AllowedHosts: new[] { "example.com" },
                PersistTargetProfile: true,
                RememberCredentials: true,
                AutomaticRelogin: false,
                PauseImmediatelyOnLogout: true,
                ShortWindowFailureThreshold: 3,
                FailureWindow: TimeSpan.FromMinutes(15),
                Cooldown: TimeSpan.FromMinutes(10),
                ClearSiteDataWhenProjectDeleted: true,
                EgressPolicy: EgressPolicy.Offline());
            return await Projects.CreateAsync(project, policy);
        }

        public string RegistryPath(Guid projectId) =>
            Path.Combine(Paths.ProjectRoot(projectId), "browser", "login-recipes.json");

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}