using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectResearchBrowserAtomicPacketAdapterTests
{
    [TestMethod]
    public async Task ExecuteAsync_PrefersAtomicAdapterAndZeroesCapturedLeaseAfterSuccess()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = Recipe();
        var secretValue = new string('q', 24);
        await fixture.Vault.AddAsync(projectId, "Primary", recipe.LoginUri, "fixture-user@example.com", secretValue);
        var adapter = new AtomicAdapter(recipe.LoginUri);

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(projectId, recipe, Policy(), true, true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.Submitted, result.Status);
        Assert.AreEqual(1, adapter.AtomicCalls);
        Assert.IsFalse(adapter.LegacyCalled);
        Assert.IsTrue(adapter.CapturedUserName.Span.ToArray().All(ch => ch == '\0'));
        Assert.IsTrue(adapter.CapturedSecret.Span.ToArray().All(ch => ch == '\0'));
    }

    [TestMethod]
    public async Task ExecuteAsync_AtomicAdapterExceptionStillZeroesCapturedLease()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = Recipe();
        var secretValue = new string('z', 28);
        await fixture.Vault.AddAsync(projectId, "Primary", recipe.LoginUri, "fixture-user@example.com", secretValue);
        var adapter = new AtomicAdapter(recipe.LoginUri) { ThrowAfterCapture = true };

        await ExpectThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(projectId, recipe, Policy(), true, true),
            adapter));

        Assert.AreEqual(1, adapter.AtomicCalls);
        Assert.IsFalse(adapter.LegacyCalled);
        Assert.IsTrue(adapter.CapturedUserName.Span.ToArray().All(ch => ch == '\0'));
        Assert.IsTrue(adapter.CapturedSecret.Span.ToArray().All(ch => ch == '\0'));
    }

    [TestMethod]
    public async Task ExecuteAsync_AtomicAdapterStillNavigatesBeforeCredentialTransfer()
    {
        using var fixture = new Fixture();
        var projectId = Guid.NewGuid();
        var recipe = Recipe();
        var secretValue = new string('n', 20);
        await fixture.Vault.AddAsync(projectId, "Primary", recipe.LoginUri, "fixture-user@example.com", secretValue);
        var adapter = new AtomicAdapter(new Uri("https://example.com/app"));

        var result = await fixture.Coordinator.ExecuteAsync(
            new ProjectLoginAutomationRequest(projectId, recipe, Policy(), true, true),
            adapter);

        Assert.AreEqual(ProjectLoginAutomationStatus.Submitted, result.Status);
        Assert.AreEqual(1, adapter.NavigateCalls);
        Assert.AreEqual(recipe.LoginUri, adapter.CurrentUri);
        Assert.AreEqual(1, adapter.AtomicCalls);
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

        Assert.Fail($"Expected {typeof(TException).Name}.");
        throw new InvalidOperationException("Unreachable test path.");
    }

    private sealed class AtomicAdapter : IResearchBrowserCredentialPacketAdapter
    {
        public AtomicAdapter(Uri? currentUri) => CurrentUri = currentUri;

        public Uri? CurrentUri { get; private set; }
        public int AtomicCalls { get; private set; }
        public int NavigateCalls { get; private set; }
        public bool LegacyCalled { get; private set; }
        public bool ThrowAfterCapture { get; init; }
        public ReadOnlyMemory<char> CapturedUserName { get; private set; }
        public ReadOnlyMemory<char> CapturedSecret { get; private set; }

        public Task NavigateAsync(Uri loginUri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NavigateCalls++;
            CurrentUri = loginUri;
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
            CapturedUserName = userName;
            CapturedSecret = password;
            if (ThrowAfterCapture)
            {
                throw new InvalidOperationException("synthetic adapter failure");
            }
            return Task.CompletedTask;
        }

        public Task FillAsync(string selector, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            LegacyCalled = true;
            throw new InvalidOperationException("Legacy fill path must not run for packet adapters.");
        }

        public Task SubmitAsync(string selector, CancellationToken cancellationToken = default)
        {
            LegacyCalled = true;
            throw new InvalidOperationException("Legacy submit path must not run for packet adapters.");
        }
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
            SecretStore = new InMemorySecretStore();
            Vault = new ProjectCredentialVault(paths, SecretStore);
            Coordinator = new ProjectResearchBrowserLoginCoordinator(new ProjectCredentialAutofillBroker(Vault));
        }

        public string Root { get; }
        public InMemorySecretStore SecretStore { get; }
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

    private sealed class InMemorySecretStore : IProjectCredentialSecretStore
    {
        private readonly Dictionary<(Guid ProjectId, Guid CredentialId), ProjectCredentialSecret> _entries = new();

        public Task SaveAsync(Guid projectId, Guid credentialId, ProjectCredentialSecret secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries[(projectId, credentialId)] = secret;
            return Task.CompletedTask;
        }

        public Task<ProjectCredentialSecret?> ReadAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
}