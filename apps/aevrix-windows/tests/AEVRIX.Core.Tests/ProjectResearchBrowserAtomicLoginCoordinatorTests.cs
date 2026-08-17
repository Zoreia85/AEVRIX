using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectResearchBrowserAtomicLoginCoordinatorTests
{
    [TestMethod]
    public async Task ExecuteAsync_AtomicAdapterUsesSingleSecretOperationAndZeroesLeaseAfterSuccess()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = Recipe();
        await fixture.Vault.AddAsync(projectId, "Primary", recipe.LoginUri, "fixture-user@example.com", new string('s', 24));
        fixture.SecretStore.ResetReadCount();
        var adapter = new AtomicAdapter(new Uri("https://example.com/login"));

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(projectId, recipe, Policy(), true, true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.Submitted, result.Status);
        Assert.AreEqual(1, fixture.SecretStore.ReadCount);
        Assert.AreEqual(1, adapter.AtomicCalls);
        Assert.AreEqual(0, adapter.LegacyCalls);
        Assert.IsTrue(adapter.CapturedUser.Span.ToArray().All(character => character == '\0'));
        Assert.IsTrue(adapter.CapturedSecret.Span.ToArray().All(character => character == '\0'));
    }

    [TestMethod]
    public async Task ExecuteAsync_AtomicAdapterFailureStillZeroesCredentialLease()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = Recipe();
        await fixture.Vault.AddAsync(projectId, "Primary", recipe.LoginUri, "fixture-user@example.com", new string('x', 24));
        fixture.SecretStore.ResetReadCount();
        var adapter = new AtomicAdapter(recipe.LoginUri) { ThrowDuringAtomicOperation = true };

        await ExpectThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(projectId, recipe, Policy(), true, true),
            adapter));

        Assert.AreEqual(1, fixture.SecretStore.ReadCount);
        Assert.AreEqual(1, adapter.AtomicCalls);
        Assert.AreEqual(0, adapter.LegacyCalls);
        Assert.IsTrue(adapter.CapturedUser.Span.ToArray().All(character => character == '\0'));
        Assert.IsTrue(adapter.CapturedSecret.Span.ToArray().All(character => character == '\0'));
    }

    [TestMethod]
    public async Task ExecuteAsync_AtomicAdapterStillNavigatesBeforeOpeningFormWhenNeeded()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = Recipe();
        await fixture.Vault.AddAsync(projectId, "Primary", recipe.LoginUri, "fixture-user@example.com", new string('z', 20));
        var adapter = new AtomicAdapter(new Uri("https://example.com/app"));

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(projectId, recipe, Policy(), true, true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.Submitted, result.Status);
        CollectionAssert.AreEqual(new[] { "navigate", "atomic" }, adapter.Events);
    }

    private static LoginRecipe Recipe() => new(
        TargetId: "target:web",
        LoginUri: new Uri("https://example.com/login"),
        UsernameSelector: "#user",
        PasswordSelector: "#secret",
        SubmitSelector: "#submit",
        AuthenticatedUrlMarkers: Array.Empty<string>(),
        AuthenticatedTextMarkers: Array.Empty<string>(),
        LoggedOutUrlMarkers: Array.Empty<string>(),
        LoggedOutTextMarkers: Array.Empty<string>(),
        LearnedAt: DateTimeOffset.UtcNow);

    private static ResearchBrowserPolicy Policy() => new ResearchBrowserPolicy(
        TargetId: "target:web",
        AllowedHosts: new[] { "example.com" },
        PersistTargetProfile: true,
        RememberCredentials: true,
        AutomaticRelogin: false,
        PauseImmediatelyOnLogout: true,
        ShortWindowFailureThreshold: 3,
        FailureWindow: TimeSpan.FromMinutes(15),
        Cooldown: TimeSpan.FromMinutes(10),
        ClearSiteDataWhenProjectDeleted: true,
        EgressPolicy: EgressPolicy.Offline()).Validate();

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

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aevrix-atomic-login-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class AtomicAdapter : IResearchBrowserAtomicLoginFormAdapter
    {
        public AtomicAdapter(Uri? currentUri) => CurrentUri = currentUri;

        public Uri? CurrentUri { get; private set; }
        public bool ThrowDuringAtomicOperation { get; init; }
        public int AtomicCalls { get; private set; }
        public int LegacyCalls { get; private set; }
        public ReadOnlyMemory<char> CapturedUser { get; private set; }
        public ReadOnlyMemory<char> CapturedSecret { get; private set; }
        public List<string> Events { get; } = new();

        public Task NavigateAsync(Uri loginUri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentUri = loginUri;
            Events.Add("navigate");
            return Task.CompletedTask;
        }

        public Task FillCredentialsAndSubmitAsync(
            LoginRecipe recipe,
            ReadOnlyMemory<char> userName,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AtomicCalls++;
            CapturedUser = userName;
            CapturedSecret = password;
            Events.Add("atomic");
            if (ThrowDuringAtomicOperation)
            {
                throw new InvalidOperationException("synthetic atomic adapter failure");
            }
            return Task.CompletedTask;
        }

        public Task FillAsync(string selector, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            LegacyCalls++;
            Assert.Fail("Legacy FillAsync must not run when atomic adapter is available.");
            return Task.CompletedTask;
        }

        public Task SubmitAsync(string selector, CancellationToken cancellationToken = default)
        {
            LegacyCalls++;
            Assert.Fail("Legacy SubmitAsync must not run when atomic adapter is available.");
            return Task.CompletedTask;
        }
    }
}
