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

        var stored = await fixture.Store.UpsertAsync(
            project.Project.Id,
            Recipe("target-web", "https://EXAMPLE.com:443/login?return=%2Fapp#form"));
        var resolved = await fixture.Store.ResolveAsync(
            project.Project.Id,
            new Uri("https://example.com/login?csrf=temporary#other"));

        Assert.AreEqual("https://example.com/login", stored.CanonicalLoginUri);
        Assert.IsNotNull(resolved);
        Assert.AreEqual("#username", resolved.Recipe.UsernameSelector);
        Assert.AreEqual("https://example.com/login", resolved.Recipe.LoginUri.AbsoluteUri.TrimEnd('/'));
    }

    [TestMethod]
    public async Task Upsert_SameCanonicalUrlReplacesWithoutDuplicate()
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

        await ExpectThrowsAsync<InvalidDataException>(() =>
            fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-other", "https://example.com/login")));

        Assert.IsFalse(File.Exists(fixture.RegistryPath(project.Project.Id)));
    }

    [TestMethod]
    public async Task Upsert_HostOutsideAllowlistFailsClosed()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");

        var ex = await ExpectThrowsAsync<InvalidDataException>(() =>
            fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://other.example/login")));

        StringAssert.Contains(ex.Message, "navigation_host_not_allowed");
        Assert.IsFalse(File.Exists(fixture.RegistryPath(project.Project.Id)));
    }

    [TestMethod]
    public async Task Store_IsolatesSameTargetAndUrlAcrossProjects()
    {
        using var fixture = new Fixture();
        var projectA = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        var projectB = await fixture.CreateProjectAsync("B", "target-web", "example.com");
        await fixture.Store.UpsertAsync(
            projectA.Project.Id,
            Recipe("target-web", "https://example.com/login") with { UsernameSelector = "#project-a" });
        await fixture.Store.UpsertAsync(
            projectB.Project.Id,
            Recipe("target-web", "https://example.com/login") with { UsernameSelector = "#project-b" });

        var a = await fixture.Store.ResolveAsync(projectA.Project.Id, new Uri("https://example.com/login"));
        var b = await fixture.Store.ResolveAsync(projectB.Project.Id, new Uri("https://example.com/login"));

        Assert.AreEqual("#project-a", a!.Recipe.UsernameSelector);
        Assert.AreEqual("#project-b", b!.Recipe.UsernameSelector);
    }

    [TestMethod]
    public async Task RegistryContainsSelectorsButNoCredentialValueProperties()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));

        var json = await File.ReadAllTextAsync(fixture.RegistryPath(project.Project.Id));
        using var document = JsonDocument.Parse(json);
        var propertyNames = EnumeratePropertyNames(document.RootElement)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(propertyNames.Contains("usernameSelector"));
        Assert.IsTrue(propertyNames.Contains("passwordSelector"));
        Assert.IsFalse(propertyNames.Contains("userName"));
        Assert.IsFalse(propertyNames.Contains("password"));
        Assert.IsFalse(propertyNames.Contains("credentialSecret"));
    }

    [TestMethod]
    public async Task List_CorruptCrossProjectRegistryFailsClosed()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));
        var path = fixture.RegistryPath(project.Project.Id);
        var json = await File.ReadAllTextAsync(path);
        var corrupted = ReplaceFirst(
            json,
            project.Project.Id.ToString(),
            Guid.NewGuid().ToString(),
            StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(path, corrupted);

        await ExpectThrowsAsync<InvalidDataException>(() => fixture.Store.ListAsync(project.Project.Id));
    }

    [TestMethod]
    public async Task List_CorruptRecipeTargetFailsAtReadTime()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));
        var path = fixture.RegistryPath(project.Project.Id);
        var json = await File.ReadAllTextAsync(path);
        Assert.IsTrue(json.Contains("target-web", StringComparison.Ordinal));
        await File.WriteAllTextAsync(path, json.Replace("target-web", "target-corrupt", StringComparison.Ordinal));

        await ExpectThrowsAsync<InvalidDataException>(() => fixture.Store.ListAsync(project.Project.Id));
    }

    [TestMethod]
    public async Task List_CorruptRecipeHostFailsAtReadTime()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));
        var path = fixture.RegistryPath(project.Project.Id);
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("example.com", "other.example", StringComparison.Ordinal));

        var ex = await ExpectThrowsAsync<InvalidDataException>(() => fixture.Store.ResolveAsync(
            project.Project.Id,
            new Uri("https://example.com/login")));
        StringAssert.Contains(ex.Message, "navigation_host_not_allowed");
    }

    [TestMethod]
    public async Task Upsert_OversizedSelectorIsRejected()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        var recipe = Recipe("target-web", "https://example.com/login") with
        {
            UsernameSelector = "#" + new string('x', 512)
        };

        await ExpectThrowsAsync<ArgumentException>(() => fixture.Store.UpsertAsync(project.Project.Id, recipe));
    }

    [TestMethod]
    public async Task Delete_IsIdempotentAndPreservesOtherRecipe()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/admin/login"));

        await fixture.Store.DeleteAsync(project.Project.Id, new Uri("https://example.com/login?transient=1"));
        await fixture.Store.DeleteAsync(project.Project.Id, new Uri("https://example.com/login"));
        var items = await fixture.Store.ListAsync(project.Project.Id);

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("https://example.com/admin/login", items[0].CanonicalLoginUri);
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static LoginRecipe Recipe(string targetId, string uri) => new(
        TargetId: targetId,
        LoginUri: new Uri(uri),
        UsernameSelector: "#username",
        PasswordSelector: "#secret-field",
        SubmitSelector: "button[type='submit']",
        AuthenticatedUrlMarkers: new[] { "/app" },
        AuthenticatedTextMarkers: new[] { "Dashboard" },
        LoggedOutUrlMarkers: new[] { "/login" },
        LoggedOutTextMarkers: new[] { "Sign in" },
        LearnedAt: DateTimeOffset.UtcNow);

    private static async Task<TException> ExpectThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            return ex;
        }

        Assert.Fail($"Expected exception {typeof(TException).Name} was not thrown.");
        throw new InvalidOperationException("Unreachable test path.");
    }

    private static string ReplaceFirst(
        string source,
        string oldValue,
        string newValue,
        StringComparison comparison)
    {
        var index = source.IndexOf(oldValue, comparison);
        Assert.IsTrue(index >= 0);
        return source[..index] + newValue + source[(index + oldValue.Length)..];
    }

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