using System.Text.Json;
using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectLoginRecipeStoreTests
{
    [TestMethod]
    public async Task UpsertAndResolve_CanonicalizesTransientLoginUrlParts()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        var recipe = Recipe("target-web", "https://EXAMPLE.com:443/login?return=%2Fapp#form");

        var stored = await fixture.Store.UpsertAsync(project.Project.Id, recipe);
        var resolved = await fixture.Store.ResolveAsync(
            project.Project.Id,
            new Uri("https://example.com/login?csrf=temporary#other"));

        Assert.AreEqual("https://example.com/login", stored.CanonicalLoginUri);
        Assert.IsNotNull(resolved);
        Assert.AreEqual("#username", resolved.Recipe.UsernameSelector);
        Assert.AreEqual("https://example.com/login", resolved.Recipe.LoginUri.AbsoluteUri.TrimEnd('/'));
    }

    [TestMethod]
    public async Task Upsert_SameCanonicalLoginUrlReplacesRecipeWithoutDuplicate()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));
        await fixture.Store.UpsertAsync(
            project.Project.Id,
            Recipe("target-web", "https://example.com/login?next=%2Fhome") with { UsernameSelector = "#email" });

        var items = await fixture.Store.ListAsync(project.Project.Id);

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("#email", items[0].Recipe.UsernameSelector);
    }

    [TestMethod]
    public async Task Upsert_TargetMismatchFailsBeforeRegistryCreation()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-other", "https://example.com/login")));

        Assert.IsFalse(File.Exists(fixture.RegistryPath(project.Project.Id)));
    }

    [TestMethod]
    public async Task Upsert_HostOutsideProjectAllowlistFailsClosed()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://other.example/login")));

        StringAssert.Contains(ex.Message, "navigation_host_not_allowed");
        Assert.IsFalse(File.Exists(fixture.RegistryPath(project.Project.Id)));
    }

    [TestMethod]
    public async Task Store_IsolatesRecipesAcrossProjectsWithSameTargetAndHost()
    {
        using var fixture = new Fixture();
        var projectA = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        var projectB = await fixture.CreateProjectAsync("B", "target-web", "example.com");
        await fixture.Store.UpsertAsync(projectA.Project.Id, Recipe("target-web", "https://example.com/login") with { UsernameSelector = "#a" });
        await fixture.Store.UpsertAsync(projectB.Project.Id, Recipe("target-web", "https://example.com/login") with { UsernameSelector = "#b" });

        var a = await fixture.Store.ResolveAsync(projectA.Project.Id, new Uri("https://example.com/login"));
        var b = await fixture.Store.ResolveAsync(projectB.Project.Id, new Uri("https://example.com/login"));

        Assert.AreEqual("#a", a!.Recipe.UsernameSelector);
        Assert.AreEqual("#b", b!.Recipe.UsernameSelector);
    }

    [TestMethod]
    public async Task PersistedRegistryContainsSelectorsButNoCredentialSecretFields()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));

        var json = await File.ReadAllTextAsync(fixture.RegistryPath(project.Project.Id));

        StringAssert.Contains(json, "usernameSelector");
        Assert.IsFalse(json.Contains("password\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("userName\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task List_CorruptCrossProjectRegistryFailsClosed()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));
        var path = fixture.RegistryPath(project.Project.Id);
        var json = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(json);
        var corrupted = json.Replace(
            project.Project.Id.ToString(),
            Guid.NewGuid().ToString(),
            StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(path, corrupted);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => fixture.Store.ListAsync(project.Project.Id));
    }

    [TestMethod]
    public async Task Upsert_RejectsOversizedSelector()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        var recipe = Recipe("target-web", "https://example.com/login") with
        {
            UsernameSelector = "#" + new string('a', 512)
        };

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            fixture.Store.UpsertAsync(project.Project.Id, recipe));
    }

    private static LoginRecipe Recipe(string targetId, string uri) => new(
        TargetId: targetId,
        LoginUri: new Uri(uri),
        UsernameSelector: "#username",
        PasswordSelector: "#password",
        SubmitSelector: "button[type='submit']",
        AuthenticatedUrlMarkers: new[] { "/app" },
        AuthenticatedTextMarkers: new[] { "Dashboard" },
        LoggedOutUrlMarkers: new[] { "/login" },
        LoggedOutTextMarkers: new[] { "Sign in" },
        LearnedAt: DateTimeOffset.UtcNow);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aevrix-login-recipe-store-tests", Guid.NewGuid().ToString("N"));
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
        }

        public string Root { get; }
        public AevrixDataPaths Paths { get; }
        public ProjectRepository Projects { get; }
        public ProjectLoginRecipeStore Store { get; }

        public async Task<ProjectEnvelope> CreateProjectAsync(string name, string targetId, params string[] allowedHosts)
        {
            var project = CaptureProject.CreateWeb(name, targetId, new Uri($"https://{allowedHosts[0]}/"));
            var policy = new ResearchBrowserPolicy(
                TargetId: targetId,
                AllowedHosts: allowedHosts,
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