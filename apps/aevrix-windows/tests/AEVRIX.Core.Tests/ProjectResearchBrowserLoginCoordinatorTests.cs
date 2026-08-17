using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectResearchBrowserLoginCoordinatorTests
{
    [TestMethod]
    public async Task ExecuteAsync_AuthorizedLoginNavigatesFillsAndSubmits()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = CreateRecipe();
        await fixture.Vault.AddAsync(projectId, "Administrador", recipe.LoginUri, "marcus@example.com", "secret-password");
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(new Uri("https://example.com/app"));

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(
                projectId,
                recipe,
                CreatePolicy(automaticRelogin: true),
                ProjectExecutionAuthorized: true,
                CredentialAutofillAuthorized: true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.Submitted, result.Status);
        Assert.AreEqual(1, fixture.SecretStore.ReadCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "navigate:https://example.com/login",
                "fill:#username:marcus@example.com",
                "fill:#password:secret-password",
                "submit:#submit"
            },
            adapter.Events);
    }

    [TestMethod]
    public async Task ExecuteAsync_AlreadyAtCanonicalLoginUrlDoesNotNavigate()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = CreateRecipe();
        await fixture.Vault.AddAsync(projectId, "Conta", recipe.LoginUri, "user", "password");
        var adapter = new FakeAdapter(new Uri("https://EXAMPLE.com:443/login?return=%2Fapp#form"));

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(
                projectId,
                recipe,
                CreatePolicy(automaticRelogin: false),
                ProjectExecutionAuthorized: true,
                CredentialAutofillAuthorized: true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.Submitted, result.Status);
        Assert.IsFalse(adapter.Events.Any(item => item.StartsWith("navigate:", StringComparison.Ordinal)));
        Assert.AreEqual(3, adapter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_ProjectAuthorizationBlockedPerformsNoSecretReadOrBrowserAction()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = CreateRecipe();
        await fixture.Vault.AddAsync(projectId, "Conta", recipe.LoginUri, "user", "password");
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(null);

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(
                projectId,
                recipe,
                CreatePolicy(automaticRelogin: true),
                ProjectExecutionAuthorized: false,
                CredentialAutofillAuthorized: true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.BlockedByPolicy, result.Status);
        Assert.AreEqual("project_login_not_authorized", result.Code);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.AreEqual(0, adapter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_CredentialPersistenceDisabledBlocksBeforeSecretRead()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = CreateRecipe();
        await fixture.Vault.AddAsync(projectId, "Conta", recipe.LoginUri, "user", "password");
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(null);
        var policy = ResearchBrowserPolicy.SecureDefault(
            "target:web",
            new[] { "example.com" },
            EgressPolicy.Offline());

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(
                projectId,
                recipe,
                policy,
                ProjectExecutionAuthorized: true,
                CredentialAutofillAuthorized: true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.BlockedByPolicy, result.Status);
        Assert.AreEqual("credential_persistence_not_enabled", result.Code);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.AreEqual(0, adapter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_AutomaticReloginRequiresExplicitPolicyPermission()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = CreateRecipe();
        await fixture.Vault.AddAsync(projectId, "Conta", recipe.LoginUri, "user", "password");
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(null);

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(
                projectId,
                recipe,
                CreatePolicy(automaticRelogin: false),
                ProjectExecutionAuthorized: true,
                CredentialAutofillAuthorized: true,
                IsAutomaticRelogin: true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.BlockedByPolicy, result.Status);
        Assert.AreEqual("automatic_relogin_not_enabled", result.Code);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.AreEqual(0, adapter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_AmbiguousAccountsNeverReachBrowser()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = CreateRecipe();
        await fixture.Vault.AddAsync(projectId, "Conta A", recipe.LoginUri, "a", "password-a", makeDefaultForLoginUri: false);
        await fixture.Vault.AddAsync(projectId, "Conta B", recipe.LoginUri, "b", "password-b", makeDefaultForLoginUri: false);
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(null);

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(
                projectId,
                recipe,
                CreatePolicy(automaticRelogin: true),
                ProjectExecutionAuthorized: true,
                CredentialAutofillAuthorized: true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.AccountSelectionRequired, result.Status);
        Assert.AreEqual(2, result.Candidates.Count);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.AreEqual(0, adapter.Events.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_TargetMismatchBlocksBeforeSecretRead()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = CreateRecipe(targetId: "target:other");
        await fixture.Vault.AddAsync(projectId, "Conta", recipe.LoginUri, "user", "password");
        fixture.SecretStore.ResetReadCount();
        var adapter = new FakeAdapter(null);

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(
                projectId,
                recipe,
                CreatePolicy(automaticRelogin: true),
                ProjectExecutionAuthorized: true,
                CredentialAutofillAuthorized: true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.BlockedByPolicy, result.Status);
        Assert.AreEqual("login_recipe_target_mismatch", result.Code);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.AreEqual(0, adapter.Events.Count);
    }

    private static LoginRecipe CreateRecipe(string targetId = "target:web") => new(
        TargetId: targetId,
        LoginUri: new Uri("https://example.com/login"),
        UsernameSelector: "#username",
        PasswordSelector: "#password",
        SubmitSelector: "#submit",
        AuthenticatedUrlMarkers: new[] { "/app" },
        AuthenticatedTextMarkers: Array.Empty<string>(),
        LoggedOutUrlMarkers: new[] { "/login" },
        LoggedOutTextMarkers: Array.Empty<string>(),
        LearnedAt: DateTimeOffset.UtcNow);

    private static ResearchBrowserPolicy CreatePolicy(bool automaticRelogin) => new(
        TargetId: "target:web",
        AllowedHosts: new[] { "example.com" },
        PersistTargetProfile: true,
        RememberCredentials: true,
        AutomaticRelogin: automaticRelogin,
        PauseImmediatelyOnLogout: true,
        ShortWindowFailureThreshold: 3,
        FailureWindow: TimeSpan.FromMinutes(15),
        Cooldown: TimeSpan.FromMinutes(10),
        ClearSiteDataWhenProjectDeleted: true,
        EgressPolicy: EgressPolicy.Offline()).Validate();

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aevrix-login-coordinator-tests", Guid.NewGuid().ToString("N"));
            var paths = new AevrixDataPaths(
                Root,
                Path.Combine(Root, "Projects"),
                Path.Combine(Root, "Vault"),
                Path.Combine(Root, "BrowserProfiles"),
                Path.Combine(Root, "Engine"),
                Path.Combine(Root, "Updates"),
                Path.Combine(Root, "Logs"),
                Path.Combine(Root, "Cache")).EnsureCreated();
            SecretStore = new CountingSecretStore();
            Vault = new ProjectCredentialVault(paths, SecretStore);
            Coordinator = new ProjectResearchBrowserLoginCoordinator(new ProjectCredentialAutofillBroker(Vault));
        }

        public string Root { get; }
        public CountingSecretStore SecretStore { get; }
        public ProjectCredentialVault Vault { get; }
        public ProjectResearchBrowserLoginCoordinator Coordinator { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
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
            Events.Add("navigate:" + loginUri.AbsoluteUri);
            return Task.CompletedTask;
        }

        public Task FillAsync(string selector, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"fill:{selector}:{new string(value.Span)}");
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
