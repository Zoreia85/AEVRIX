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
        var stored = await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://EXAMPLE.com:443/login?return=%2Fapp#form"));
        var resolved = await fixture.Store.ResolveAsync(project.Project.Id, new Uri("https://example.com/login?csrf=temporary#other"));
        Assert.AreEqual("https://example.com/login", stored.CanonicalLoginUri);
        Assert.IsNotNull(resolved);
        Assert.AreEqual("#username", resolved.Recipe.UsernameSelector);
    }

    [TestMethod]
    public async Task Upsert_SameCanonicalUrlReplacesWithoutDuplicate()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login?next=%2Fhome") with { UsernameSelector = "#email" });
        var items = await fixture.Store.ListAsync(project.Project.Id);
        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("#email", items[0].Recipe.UsernameSelector);
    }

    [TestMethod]
    public async Task Upsert_TargetMismatchFailsBeforeRegistryCreation()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await ExpectThrowsAsync<InvalidDataException>(() => fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-other", "https://example.com/login")));
        Assert.IsFalse(File.Exists(fixture.RegistryPath(project.Project.Id)));
    }

    [TestMethod]
    public async Task Upsert_HostOutsideAllowlistFailsClosed()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        var ex = await ExpectThrowsAsync<InvalidDataException>(() => fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://other.example/login")));
        StringAssert.Contains(ex.Message, "navigation_host_not_allowed");
    }

    [TestMethod]
    public async Task Store_IsolatesSameTargetAndUrlAcrossProjects()
    {
        using var fixture = new Fixture();
        var a = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        var b = await fixture.CreateProjectAsync("B", "target-web", "example.com");
        await fixture.Store.UpsertAsync(a.Project.Id, Recipe("target-web", "https://example.com/login") with { UsernameSelector = "#a" });
        await fixture.Store.UpsertAsync(b.Project.Id, Recipe("target-web", "https://example.com/login") with { UsernameSelector = "#b" });
        Assert.AreEqual("#a", (await fixture.Store.ResolveAsync(a.Project.Id, new Uri("https://example.com/login")))!.Recipe.UsernameSelector);
        Assert.AreEqual("#b", (await fixture.Store.ResolveAsync(b.Project.Id, new Uri("https://example.com/login")))!.Recipe.UsernameSelector);
    }

    [TestMethod]
    public async Task RegistryContainsSelectorsButNoCredentialValueProperties()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.RegistryPath(project.Project.Id)));
        var names = EnumeratePropertyNames(document.RootElement).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(names.Contains("usernameSelector"));
        Assert.IsTrue(names.Contains("passwordSelector"));
        Assert.IsFalse(names.Contains("userName"));
        Assert.IsFalse(names.Contains("password"));
        Assert.IsFalse(names.Contains("credentialSecret"));
    }

    [TestMethod]
    public async Task List_CorruptCrossProjectRegistryFailsClosed()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync("A", "target-web", "example.com");
        await fixture.Store.UpsertAsync(project.Project.Id, Recipe("target-web", "https://example.com/login"));
        var path = fixture.RegistryPath(project.Project.Id);
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, ReplaceFirst(json, project.Project.Id.ToString(), Guid.NewGuid().ToString(), StringComparison.OrdinalIgnoreCase));
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
        var ex = await ExpectThrowsAsync<InvalidDataException>(() => fixture.Store.ResolveAsync(project.Project.Id, new Uri("https://example.com/login")));
        StringAssert.Contains(ex.Message, "navigation_host_not_allowed");
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
                foreach (var nested in EnumeratePropertyNames(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in EnumeratePropertyNames(item)) yield return nested;
        }
    }

    private static LoginRecipe Recipe(string targetId, string uri) => new(
        targetId, new Uri(uri), "#username", "#secret-field", "button[type='submit']",
        new[] { "/app" }, new[] { "Dashboard" }, new[] { "/login" }, new[] { "Sign in" }, DateTimeOffset.UtcNow);

    private static async Task<TException> ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException ex) { return ex; }
        Assert.Fail($"Expected exception {typeof(TException).Name} was not thrown.");
        throw new InvalidOperationException("Unreachable test path.");
    }

    private static string ReplaceFirst(string source, string oldValue, string newValue, StringComparison comparison)
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
            Paths = new AevrixDataPaths(Root, Path.Combine(Root,"Projects"), Path.Combine(Root,"Vault"), Path.Combine(Root,"BrowserProfiles"), Path.Combine(Root,"Engine"), Path.Combine(Root,"Updates"), Path.Combine(Root,"Logs"), Path.Combine(Root,"Cache")).EnsureCreated();
            Projects = new ProjectRepository(Paths);
            Store = new ProjectLoginRecipeStore(Paths, Projects);
        }
        public string Root { get; }
        public AevrixDataPaths Paths { get; }
        public ProjectRepository Projects { get; }
        public ProjectLoginRecipeStore Store { get; }
        public async Task<ProjectEnvelope> CreateProjectAsync(string name, string targetId, params string[] hosts)
        {
            var project = CaptureProject.CreateWeb(name, targetId, new Uri($"https://{hosts[0]}/"));
            var policy = new ResearchBrowserPolicy(targetId, hosts, true, true, false, true, 3, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10), true, EgressPolicy.Offline());
            return await Projects.CreateAsync(project, policy);
        }
        public string RegistryPath(Guid projectId) => Path.Combine(Paths.ProjectRoot(projectId), "browser", "login-recipes.json");
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}