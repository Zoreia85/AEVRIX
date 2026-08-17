using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectCredentialAutofillBrokerTests
{
    [TestMethod]
    public async Task PrepareAsync_BlockedPolicyDoesNotReadSecret()
    {
        using var fixture = new AutofillFixture();
        var projectId = Guid.NewGuid();
        var loginUri = new Uri("https://portal.example.com/login");
        await fixture.Vault.AddAsync(projectId, "Portal", loginUri, "user", "password");
        fixture.SecretStore.ResetReadCount();

        var decision = await fixture.Broker.PrepareAsync(new ProjectCredentialAutofillRequest(
            projectId,
            loginUri,
            ProjectExecutionAuthorized: false,
            CredentialAutofillAuthorized: true));

        Assert.AreEqual(ProjectCredentialAutofillStatus.BlockedByPolicy, decision.Status);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.IsNull(decision.Credential);
    }

    [TestMethod]
    public async Task PrepareAsync_AuthorizedMatchingLoginReturnsDisposableCredential()
    {
        using var fixture = new AutofillFixture();
        var projectId = Guid.NewGuid();
        var loginUri = new Uri("https://portal.example.com/login");
        await fixture.Vault.AddAsync(projectId, "Portal", loginUri, "user", "password");
        fixture.SecretStore.ResetReadCount();

        var decision = await fixture.Broker.PrepareAsync(new ProjectCredentialAutofillRequest(
            projectId,
            new Uri("https://portal.example.com/login?return=%2Fdashboard"),
            ProjectExecutionAuthorized: true,
            CredentialAutofillAuthorized: true));

        Assert.AreEqual(ProjectCredentialAutofillStatus.Ready, decision.Status);
        Assert.AreEqual(1, fixture.SecretStore.ReadCount);
        Assert.IsNotNull(decision.Credential);
        using var credential = decision.Credential!;
        Assert.AreEqual("user", new string(credential.UserName.Span));
        Assert.AreEqual("password", new string(credential.Password.Span));
    }

    [TestMethod]
    public async Task PrepareAsync_MultipleAccountsWithoutDefaultNeverGuesses()
    {
        using var fixture = new AutofillFixture();
        var projectId = Guid.NewGuid();
        var loginUri = new Uri("https://portal.example.com/login");
        await fixture.Vault.AddAsync(projectId, "Conta A", loginUri, "a", "password-a", makeDefaultForLoginUri: false);
        await fixture.Vault.AddAsync(projectId, "Conta B", loginUri, "b", "password-b", makeDefaultForLoginUri: false);
        fixture.SecretStore.ResetReadCount();

        var decision = await fixture.Broker.PrepareAsync(new ProjectCredentialAutofillRequest(
            projectId,
            loginUri,
            ProjectExecutionAuthorized: true,
            CredentialAutofillAuthorized: true));

        Assert.AreEqual(ProjectCredentialAutofillStatus.Ambiguous, decision.Status);
        Assert.AreEqual(2, decision.Candidates.Count);
        Assert.AreEqual(0, fixture.SecretStore.ReadCount);
        Assert.IsNull(decision.Credential);
    }

    private sealed class AutofillFixture : IDisposable
    {
        public AutofillFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aevrix-autofill-tests", Guid.NewGuid().ToString("N"));
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
            Broker = new ProjectCredentialAutofillBroker(Vault);
        }

        public string Root { get; }
        public CountingSecretStore SecretStore { get; }
        public ProjectCredentialVault Vault { get; }
        public ProjectCredentialAutofillBroker Broker { get; }

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
}
