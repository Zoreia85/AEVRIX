using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectResearchBrowserLoginCoordinatorSecretCleanupTests
{
    [TestMethod]
    public async Task ExecuteAsync_BrowserFailureStillZeroesCapturedPasswordLease()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-login-cleanup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AevrixDataPaths(
                root,
                Path.Combine(root, "Projects"),
                Path.Combine(root, "Vault"),
                Path.Combine(root, "BrowserProfiles"),
                Path.Combine(root, "Engine"),
                Path.Combine(root, "Updates"),
                Path.Combine(root, "Logs"),
                Path.Combine(root, "Cache")).EnsureCreated();
            var secretStore = new InMemorySecretStore();
            var vault = new ProjectCredentialVault(paths, secretStore);
            var coordinator = new ProjectResearchBrowserLoginCoordinator(
                new ProjectCredentialAutofillBroker(vault));
            var projectId = Guid.NewGuid();
            var recipe = CreateRecipe();
            await vault.AddAsync(projectId, "Conta", recipe.LoginUri, "user", "sensitive-password");
            var adapter = new ThrowingSubmitAdapter(recipe.LoginUri);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await coordinator.ExecuteAsync(
                    new ProjectLoginAutomationRequest(
                        projectId,
                        recipe,
                        CreatePolicy(),
                        ProjectExecutionAuthorized: true,
                        CredentialAutofillAuthorized: true),
                    adapter));

            Assert.IsFalse(adapter.CapturedPassword.IsEmpty);
            Assert.IsTrue(
                adapter.CapturedPassword.Span.ToArray().All(ch => ch == '\0'),
                "The password lease must be cleared even when the browser submit fails.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static LoginRecipe CreateRecipe() => new(
        TargetId: "target:web",
        LoginUri: new Uri("https://example.com/login"),
        UsernameSelector: "#username",
        PasswordSelector: "#password",
        SubmitSelector: "#submit",
        AuthenticatedUrlMarkers: new[] { "/app" },
        AuthenticatedTextMarkers: Array.Empty<string>(),
        LoggedOutUrlMarkers: new[] { "/login" },
        LoggedOutTextMarkers: Array.Empty<string>(),
        LearnedAt: DateTimeOffset.UtcNow);

    private static ResearchBrowserPolicy CreatePolicy() => new ResearchBrowserPolicy(
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

    private sealed class ThrowingSubmitAdapter : IResearchBrowserLoginFormAdapter
    {
        public ThrowingSubmitAdapter(Uri currentUri) => CurrentUri = currentUri;
        public Uri? CurrentUri { get; }
        public ReadOnlyMemory<char> CapturedPassword { get; private set; }

        public Task NavigateAsync(Uri loginUri, CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Navigation is not expected when already at the login URL.");

        public Task FillAsync(string selector, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(selector, "#password", StringComparison.Ordinal))
            {
                CapturedPassword = value;
            }
            return Task.CompletedTask;
        }

        public Task SubmitAsync(string selector, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("simulated-browser-submit-failure");
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
