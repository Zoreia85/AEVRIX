using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectCredentialVaultTests
{
    [TestMethod]
    public void CanonicalizeLoginUri_RemovesQueryFragmentAndDefaultPort()
    {
        var canonical = ProjectCredentialVault.CanonicalizeLoginUri(
            new Uri("https://EXAMPLE.com:443/account/login?return=%2Fadmin#section"));

        Assert.AreEqual("https://example.com/account/login", canonical);
    }

    [TestMethod]
    public async Task AddAsync_StoresOnlyNonSecretMetadataOnDisk()
    {
        using var fixture = new VaultFixture();
        var projectId = Guid.NewGuid();

        var descriptor = await fixture.Vault.AddAsync(
            projectId,
            "Conta administrativa",
            new Uri("https://portal.example.com/login"),
            "marcus@example.com",
            "UltraSecret-123!",
            makeDefaultForLoginUri: true);

        var registryPath = Path.Combine(
            fixture.Paths.VaultRoot,
            "ProjectCredentials",
            projectId.ToString("N") + ".json");
        var registryText = await File.ReadAllTextAsync(registryPath);

        StringAssert.Contains(registryText, descriptor.CredentialId.ToString());
        StringAssert.Contains(registryText, "Conta administrativa");
        StringAssert.Contains(registryText, "https://portal.example.com/login");
        Assert.IsFalse(registryText.Contains("marcus@example.com", StringComparison.Ordinal));
        Assert.IsFalse(registryText.Contains("UltraSecret-123!", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ResolveForLoginAsync_IsStrictlyProjectScoped()
    {
        using var fixture = new VaultFixture();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var loginUri = new Uri("https://portal.example.com/login");

        await fixture.Vault.AddAsync(projectA, "A", loginUri, "user-a", "password-a");

        var resolution = await fixture.Vault.ResolveForLoginAsync(projectB, loginUri);

        Assert.AreEqual(ProjectCredentialResolutionStatus.NotFound, resolution.Status);
        Assert.IsNull(resolution.Credential);
    }

    [TestMethod]
    public async Task ResolveForLoginAsync_AllowsMultipleAccountsAndUsesExplicitDefault()
    {
        using var fixture = new VaultFixture();
        var projectId = Guid.NewGuid();
        var loginUri = new Uri("https://portal.example.com/login");

        var first = await fixture.Vault.AddAsync(
            projectId, "Administrador", loginUri, "admin", "admin-password", makeDefaultForLoginUri: false);
        var second = await fixture.Vault.AddAsync(
            projectId, "Financeiro", loginUri, "finance", "finance-password", makeDefaultForLoginUri: false);

        var ambiguous = await fixture.Vault.ResolveForLoginAsync(projectId, loginUri);
        Assert.AreEqual(ProjectCredentialResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.AreEqual(2, ambiguous.Candidates.Count);
        Assert.IsNull(ambiguous.Credential);

        await fixture.Vault.SetDefaultAsync(projectId, second.CredentialId);
        var resolved = await fixture.Vault.ResolveForLoginAsync(projectId, loginUri);

        Assert.AreEqual(ProjectCredentialResolutionStatus.Resolved, resolved.Status);
        Assert.IsNotNull(resolved.Credential);
        using var credential = resolved.Credential!;
        Assert.AreEqual(second.CredentialId, credential.Descriptor.CredentialId);
        Assert.AreEqual("finance", new string(credential.UserName.Span));
        Assert.AreEqual("finance-password", new string(credential.Password.Span));
        Assert.AreNotEqual(first.CredentialId, credential.Descriptor.CredentialId);
    }

    [TestMethod]
    public async Task AddAsync_NewDefaultRevokesOldDefaultForSameLoginOnly()
    {
        using var fixture = new VaultFixture();
        var projectId = Guid.NewGuid();
        var loginA = new Uri("https://portal.example.com/login");
        var loginB = new Uri("https://admin.example.com/sign-in");

        await fixture.Vault.AddAsync(projectId, "A1", loginA, "a1", "pw-a1", makeDefaultForLoginUri: true);
        var a2 = await fixture.Vault.AddAsync(projectId, "A2", loginA, "a2", "pw-a2", makeDefaultForLoginUri: true);
        var b1 = await fixture.Vault.AddAsync(projectId, "B1", loginB, "b1", "pw-b1", makeDefaultForLoginUri: true);

        var entries = await fixture.Vault.ListAsync(projectId);
        var defaultsA = entries.Where(entry => entry.CanonicalLoginUri == "https://portal.example.com/login" && entry.IsDefaultForLoginUri).ToArray();
        var defaultsB = entries.Where(entry => entry.CanonicalLoginUri == "https://admin.example.com/sign-in" && entry.IsDefaultForLoginUri).ToArray();

        Assert.AreEqual(1, defaultsA.Length);
        Assert.AreEqual(a2.CredentialId, defaultsA[0].CredentialId);
        Assert.AreEqual(1, defaultsB.Length);
        Assert.AreEqual(b1.CredentialId, defaultsB[0].CredentialId);
    }

    [TestMethod]
    public async Task ResolveForLoginAsync_IgnoresQueryAndFragmentForStableLoginMatching()
    {
        using var fixture = new VaultFixture();
        var projectId = Guid.NewGuid();

        await fixture.Vault.AddAsync(
            projectId,
            "Portal",
            new Uri("https://portal.example.com/login?source=setup"),
            "user",
            "password");

        var resolution = await fixture.Vault.ResolveForLoginAsync(
            projectId,
            new Uri("https://portal.example.com/login?return=%2Fproject#form"));

        Assert.AreEqual(ProjectCredentialResolutionStatus.Resolved, resolution.Status);
        resolution.Credential?.Dispose();
    }

    [TestMethod]
    public async Task ResolveForLoginAsync_MissingSecretFailsClosed()
    {
        using var fixture = new VaultFixture();
        var projectId = Guid.NewGuid();
        var descriptor = await fixture.Vault.AddAsync(
            projectId,
            "Portal",
            new Uri("https://portal.example.com/login"),
            "user",
            "password");
        await fixture.SecretStore.DeleteAsync(projectId, descriptor.CredentialId);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await fixture.Vault.ResolveForLoginAsync(projectId, new Uri("https://portal.example.com/login")));
    }

    [TestMethod]
    public async Task CorruptRegistryFailsClosed()
    {
        using var fixture = new VaultFixture();
        var projectId = Guid.NewGuid();
        var directory = Path.Combine(fixture.Paths.VaultRoot, "ProjectCredentials");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, projectId.ToString("N") + ".json"), "{not-json");

        await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(async () =>
            await fixture.Vault.ListAsync(projectId));
    }

    [TestMethod]
    public async Task CredentialLease_CannotBeReadAfterDispose()
    {
        using var fixture = new VaultFixture();
        var projectId = Guid.NewGuid();
        var loginUri = new Uri("https://portal.example.com/login");
        await fixture.Vault.AddAsync(projectId, "Portal", loginUri, "user", "password");
        var resolution = await fixture.Vault.ResolveForLoginAsync(projectId, loginUri);
        var lease = resolution.Credential!;

        lease.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = lease.UserName);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = lease.Password);
    }

    private sealed class VaultFixture : IDisposable
    {
        public VaultFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "aevrix-credential-vault-tests", Guid.NewGuid().ToString("N"));
            Paths = new AevrixDataPaths(
                Root,
                Path.Combine(Root, "Projects"),
                Path.Combine(Root, "Vault"),
                Path.Combine(Root, "BrowserProfiles"),
                Path.Combine(Root, "Engine"),
                Path.Combine(Root, "Updates"),
                Path.Combine(Root, "Logs"),
                Path.Combine(Root, "Cache")).EnsureCreated();
            SecretStore = new InMemorySecretStore();
            Vault = new ProjectCredentialVault(Paths, SecretStore);
        }

        public string Root { get; }
        public AevrixDataPaths Paths { get; }
        public InMemorySecretStore SecretStore { get; }
        public ProjectCredentialVault Vault { get; }

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
