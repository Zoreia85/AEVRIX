using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ProjectLocalAuthStateCleanerTests
{
    [TestMethod]
    public async Task PurgeAsync_RemovesOnlySelectedProjectCredentialsAndBrowserProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-auth-cleanup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = CreatePaths(root).EnsureCreated();
            var secretStore = new InMemorySecretStore();
            var vault = new ProjectCredentialVault(paths, secretStore);
            var cleaner = new ProjectLocalAuthStateCleaner(paths, vault);
            var projectA = Guid.NewGuid();
            var projectB = Guid.NewGuid();
            var login = new Uri("https://example.com/login");

            await vault.AddAsync(projectA, "A-Admin", login, "user-a", "password-a");
            await vault.AddAsync(projectA, "A-Finance", login, "finance-a", "finance-password-a", makeDefaultForLoginUri: false);
            await vault.AddAsync(projectB, "B-Admin", login, "user-b", "password-b");

            var browserA = paths.ProjectBrowserProfile(projectA, "portal-web");
            var browserB = paths.ProjectBrowserProfile(projectB, "portal-web");
            Directory.CreateDirectory(browserA);
            Directory.CreateDirectory(browserB);
            await File.WriteAllTextAsync(Path.Combine(browserA, "cookie.db"), "a");
            await File.WriteAllTextAsync(Path.Combine(browserB, "cookie.db"), "b");

            var result = await cleaner.PurgeAsync(projectA);

            Assert.AreEqual(projectA, result.ProjectId);
            Assert.AreEqual(2, result.CredentialsRemoved);
            Assert.IsTrue(result.BrowserProfileRemoved);
            Assert.AreEqual(0, (await vault.ListAsync(projectA)).Count);
            Assert.AreEqual(1, (await vault.ListAsync(projectB)).Count);
            Assert.IsFalse(Directory.Exists(Path.Combine(paths.BrowserProfilesRoot, projectA.ToString("N"))));
            Assert.IsTrue(Directory.Exists(Path.Combine(paths.BrowserProfilesRoot, projectB.ToString("N"))));
            Assert.AreEqual(1, secretStore.CountFor(projectB));
            Assert.AreEqual(0, secretStore.CountFor(projectA));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PurgeAsync_MissingStateIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-auth-cleanup-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = CreatePaths(root).EnsureCreated();
            var vault = new ProjectCredentialVault(paths, new InMemorySecretStore());
            var cleaner = new ProjectLocalAuthStateCleaner(paths, vault);
            var projectId = Guid.NewGuid();

            var first = await cleaner.PurgeAsync(projectId);
            var second = await cleaner.PurgeAsync(projectId);

            Assert.AreEqual(0, first.CredentialsRemoved);
            Assert.IsFalse(first.BrowserProfileRemoved);
            Assert.AreEqual(0, second.CredentialsRemoved);
            Assert.IsFalse(second.BrowserProfileRemoved);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PurgeAsync_ReparsePointFailsBeforeCredentialDeletion()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows reparse-point behavior is validated on Windows CI.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "aevrix-auth-cleanup-tests", Guid.NewGuid().ToString("N"));
        var external = Path.Combine(Path.GetTempPath(), "aevrix-auth-cleanup-external", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = CreatePaths(root).EnsureCreated();
            var secretStore = new InMemorySecretStore();
            var vault = new ProjectCredentialVault(paths, secretStore);
            var cleaner = new ProjectLocalAuthStateCleaner(paths, vault);
            var projectId = Guid.NewGuid();
            await vault.AddAsync(projectId, "Conta", new Uri("https://example.com/login"), "user", "password");

            var projectBrowserRoot = Path.Combine(paths.BrowserProfilesRoot, projectId.ToString("N"));
            Directory.CreateDirectory(projectBrowserRoot);
            Directory.CreateDirectory(external);
            var link = Path.Combine(projectBrowserRoot, "linked-target");
            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (UnauthorizedAccessException)
            {
                Assert.Inconclusive("Runner does not permit symbolic-link creation.");
                return;
            }
            catch (IOException)
            {
                Assert.Inconclusive("Runner does not permit symbolic-link creation.");
                return;
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await cleaner.PurgeAsync(projectId));
            Assert.AreEqual(1, (await vault.ListAsync(projectId)).Count, "Credential metadata must remain when cleanup preflight blocks.");
            Assert.AreEqual(1, secretStore.CountFor(projectId));
            Assert.IsTrue(Directory.Exists(external));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            if (Directory.Exists(external))
            {
                Directory.Delete(external, recursive: true);
            }
        }
    }

    private static AevrixDataPaths CreatePaths(string root) => new(
        root,
        Path.Combine(root, "Projects"),
        Path.Combine(root, "Vault"),
        Path.Combine(root, "BrowserProfiles"),
        Path.Combine(root, "Engine"),
        Path.Combine(root, "Updates"),
        Path.Combine(root, "Logs"),
        Path.Combine(root, "Cache"));

    private sealed class InMemorySecretStore : IProjectCredentialSecretStore
    {
        private readonly Dictionary<(Guid ProjectId, Guid CredentialId), ProjectCredentialSecret> _entries = new();

        public int CountFor(Guid projectId) => _entries.Keys.Count(key => key.ProjectId == projectId);

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
