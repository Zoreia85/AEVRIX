using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectCurrentPageLoginServiceTests
{
    [TestMethod]
    public async Task ExecuteAsync_UnauthorizedFlowPerformsZeroSecretReads()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        await fixture.AddRecipeAndCredentialAsync(project.Project.Id);
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(new Uri("https://example.com/login"));

        var result = await fixture.Service.ExecuteAsync(
            new(project.Project.Id, ProjectExecutionAuthorized: false, CredentialAutofillAuthorized: true),
            adapter);

        Assert.AreEqual(ProjectCurrentPageLoginStatus.BlockedByPolicy, result.Status);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.AreEqual(0, adapter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_CurrentPageAutomaticallyResolvesRecipeAndSubmits()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        await fixture.AddRecipeAndCredentialAsync(project.Project.Id);
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(new Uri("https://EXAMPLE.com:443/login?return=%2Fapp#form"));

        var result = await fixture.Service.ExecuteAsync(new(project.Project.Id, true, true), adapter);

        Assert.AreEqual(ProjectCurrentPageLoginStatus.Submitted, result.Status);
        Assert.AreEqual(1, fixture.SecretStore.ReadCount);
        CollectionAssert.AreEqual(
            new[] { "fill:#user", "fill:#secret", "submit:#submit" },
            adapter.Events);
    }

    [TestMethod]
    public async Task ExecuteAsync_NoRecipeForCurrentPagePerformsZeroSecretReads()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        await fixture.AddRecipeAndCredentialAsync(project.Project.Id);
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(new Uri("https://example.com/other"));

        var result = await fixture.Service.ExecuteAsync(new(project.Project.Id, true, true), adapter);

        Assert.AreEqual(ProjectCurrentPageLoginStatus.RecipeNotFound, result.Status);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.AreEqual(0, adapter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_CurrentPageOutsideAllowlistBlocksBeforeSecretRead()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        await fixture.AddRecipeAndCredentialAsync(project.Project.Id);
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(new Uri("https://other.example/login"));

        var result = await fixture.Service.ExecuteAsync(new(project.Project.Id, true, true), adapter);

        Assert.AreEqual(ProjectCurrentPageLoginStatus.BlockedByPolicy, result.Status);
        Assert.AreEqual("navigation_host_not_allowed", result.Code);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_AmbiguousCredentialsReturnsSelectionWithoutReadingSecret()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        var recipe = Recipe();
        await fixture.Store.UpsertAsync(project.Project.Id, recipe);
        await fixture.Vault.AddAsync(
            project.Project.Id,
            "A",
            recipe.LoginUri,
            "fixture-a@example.com",
            new string('a', 16),
            makeDefaultForLoginUri: false);
        await fixture.Vault.AddAsync(
            project.Project.Id,
            "B",
            recipe.LoginUri,
            "fixture-b@example.com",
            new string('b', 16),
            makeDefaultForLoginUri: false);
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(recipe.LoginUri);

        var result = await fixture.Service.ExecuteAsync(new(project.Project.Id, true, true), adapter);

        Assert.AreEqual(ProjectCurrentPageLoginStatus.AccountSelectionRequired, result.Status);
        Assert.AreEqual(2, result.Candidates.Count);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.AreEqual(0, adapter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_AutomaticReloginRequiresPolicyPermission()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync(automaticRelogin: false);
        await fixture.AddRecipeAndCredentialAsync(project.Project.Id);
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(new Uri("https://example.com/login"));

        var result = await fixture.Service.ExecuteAsync(
            new(project.Project.Id, true, true, IsAutomaticRelogin: true),
            adapter);

        Assert.AreEqual(ProjectCurrentPageLoginStatus.BlockedByPolicy, result.Status);
        Assert.AreEqual("automatic_relogin_not_enabled", result.Code);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.AreEqual(0, adapter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_NoActiveBrowserPageDoesNotReadSecret()
    {
        using var fixture = new Fixture();
        var project = await fixture.CreateProjectAsync();
        await fixture.AddRecipeAndCredentialAsync(project.Project.Id);
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(null);

        var result = await fixture.Service.ExecuteAsync(new(project.Project.Id, true, true), adapter);

        Assert.AreEqual(ProjectCurrentPageLoginStatus.NoActivePage, result.Status);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
    }

    private static LoginRecipe Recipe() => new(
        TargetId: "target-web",
        LoginUri: new Uri("https://example.com/login"),
        UsernameSelector: "#user",
        PasswordSelector: "#secret",
        SubmitSelector: "#submit",
        AuthenticatedUrlMarkers: Array.Empty<string>(),
        AuthenticatedTextMarkers: Array.Empty<string>(),
        LoggedOutUrlMarkers: Array.Empty<string>(),
        LoggedOutTextMarkers: Array.Empty<string>(),
        LearnedAt: DateTimeOffset.UtcNow);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aevrix-current-page-login-tests", Guid.NewGuid().ToString("N"));
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
            SecretStore = new CountingSecretStore();
            Vault = new ProjectCredentialVault(Paths, SecretStore);
            Coordinator = new ProjectResearchBrowserLoginCoordinator(new ProjectCredentialAutofillBroker(Vault));
            Service = new ProjectCurrentPageLoginService(Projects, Store, Coordinator);
        }

        public string Root { get; }
        public AevrixDataPaths Paths { get; }
        public ProjectRepository Projects { get; }
        public ProjectLoginRecipeStore Store { get; }
        public CountingSecretStore SecretStore { get; }
        public ProjectCredentialVault Vault { get; }
        public ProjectResearchBrowserLoginCoordinator Coordinator { get; }
        public ProjectCurrentPageLoginService Service { get; }

        public async Task<ProjectEnvelope> CreateProjectAsync(bool automaticRelogin = false)
        {
            var project = CaptureProject.CreateWeb(
                "Current Page Login",
                "target-web",
                new Uri("https://example.com/"));
            var policy = new ResearchBrowserPolicy(
                TargetId: "target-web",
                AllowedHosts: new[] { "example.com" },
                PersistTargetProfile: true,
                RememberCredentials: true,
                AutomaticRelogin: automaticRelogin,
                PauseImmediatelyOnLogout: true,
                ShortWindowFailureThreshold: 3,
                FailureWindow: TimeSpan.FromMinutes(15),
                Cooldown: TimeSpan.FromMinutes(10),
                ClearSiteDataWhenProjectDeleted: true,
                EgressPolicy: EgressPolicy.Offline());
            return await Projects.CreateAsync(project, policy);
        }

        public async Task AddRecipeAndCredentialAsync(Guid projectId)
        {
            var recipe = Recipe();
            await Store.UpsertAsync(projectId, recipe);
            await Vault.AddAsync(
                projectId,
                "Primary",
                recipe.LoginUri,
                "fixture-user@example.com",
                new string('s', 20));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CountingSecretStore : IProjectCredentialSecretStore
    {
        private readonly Dictionary<(Guid ProjectId, Guid CredentialId), ProjectCredentialSecret> _entries = new();
        public int ReadCount { get; private set; }
        public void ResetReadCount() => ReadCount = 0;

        public Task SaveAsync(Guid projectId, Guid credentialId, ProjectCredentialSecret secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries[(projectId, credentialId)] = secret;
            return Task.CompletedTask;
        }

        public Task<ProjectCredentialSecret?> ReadAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            _entries.TryGetValue((projectId, credentialId), out var secret);
            return Task.FromResult(secret);
        }

        public Task DeleteAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.Remove((projectId, credentialId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdapter : IResearchBrowserLoginFormAdapter
    {
        public FakeAdapter(Uri? currentUri) => CurrentUri = currentUri;
        public Uri? CurrentUri { get; private set; }
        public List<string> Events { get; } = new();

        public Task NavigateAsync(Uri loginUri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentUri = loginUri;
            Events.Add("navigate");
            return Task.CompletedTask;
        }

        public Task FillAsync(string selector, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("fill:" + selector);
            return Task.CompletedTask;
        }

        public Task SubmitAsync(string selector, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("submit:" + selector);
            return Task.CompletedTask;
        }
    }
}
