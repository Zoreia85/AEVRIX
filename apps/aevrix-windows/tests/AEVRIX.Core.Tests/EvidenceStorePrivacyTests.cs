using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class EvidenceStorePrivacyTests
{
    [TestMethod]
    public async Task DefaultPolicyDoesNotPersistSourceIdentityMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-evidence-privacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var paths = Paths(root);
            var source = Path.Combine(root, "customer-marcus-private.txt");
            await File.WriteAllTextAsync(source, "privacy fixture");
            var projectId = Guid.NewGuid();
            var store = new EvidenceStore(paths);

            var artifact = await store.StoreFileAsync(
                projectId,
                "capture-privacy-001",
                source,
                EvidenceClassification.Sanitized,
                "text",
                "text/plain",
                EvidenceBasis.Observed,
                new Uri("https://example.com/account?email=person@example.com&token=secret"),
                "customer account evidence person@example.com");

            Assert.AreEqual("evidence.txt", artifact.OriginalName);
            Assert.IsNull(artifact.SourceUri);
            Assert.IsNull(artifact.Description);

            var indexPath = Path.Combine(paths.ProjectEvidenceRoot(projectId), "index.ndjson");
            var persisted = await File.ReadAllTextAsync(indexPath);
            Assert.IsFalse(persisted.Contains("customer-marcus-private", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(persisted.Contains("person@example.com", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(persisted.Contains("token=secret", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FullRetentionRequiresExplicitOptIn()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-evidence-full-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var paths = Paths(root);
            var source = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(source, "full retention fixture");
            var store = new EvidenceStore(paths, EvidenceMetadataRetention.Full);

            var artifact = await store.StoreFileAsync(
                Guid.NewGuid(),
                "capture-full-001",
                source,
                EvidenceClassification.Sanitized,
                "text",
                "text/plain",
                EvidenceBasis.Observed,
                new Uri("https://example.com/reference"),
                "explicit retention");

            Assert.AreEqual("source.txt", artifact.OriginalName);
            Assert.AreEqual("https://example.com/reference", artifact.SourceUri);
            Assert.AreEqual("explicit retention", artifact.Description);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task VerifyRejectsCrossProjectArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-evidence-verify-scope-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var paths = Paths(root);
            var source = Path.Combine(root, "scope.txt");
            await File.WriteAllTextAsync(source, "scope fixture");
            var store = new EvidenceStore(paths);
            var expectedProject = Guid.NewGuid();

            var foreign = await store.StoreFileAsync(
                Guid.NewGuid(),
                "capture-scope-001",
                source,
                EvidenceClassification.Sanitized,
                "text",
                "text/plain",
                EvidenceBasis.Observed);

            Assert.IsFalse(await store.VerifyAsync(expectedProject, foreign));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadIndexRejectsCrossProjectArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-evidence-index-scope-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var paths = Paths(root);
            var source = Path.Combine(root, "index-scope.txt");
            await File.WriteAllTextAsync(source, "index scope fixture");
            var store = new EvidenceStore(paths);
            var projectA = Guid.NewGuid();
            var projectB = Guid.NewGuid();

            await store.StoreFileAsync(
                projectB,
                "capture-index-scope-001",
                source,
                EvidenceClassification.Sanitized,
                "text",
                "text/plain",
                EvidenceBasis.Observed);

            var foreignIndex = Path.Combine(paths.ProjectEvidenceRoot(projectB), "index.ndjson");
            var poisonedIndex = Path.Combine(paths.ProjectEvidenceRoot(projectA), "index.ndjson");
            Directory.CreateDirectory(Path.GetDirectoryName(poisonedIndex)!);
            await File.WriteAllTextAsync(poisonedIndex, await File.ReadAllTextAsync(foreignIndex));

            var rejected = false;
            try
            {
                await store.ReadIndexAsync(projectA);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            Assert.IsTrue(rejected);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static AevrixDataPaths Paths(string root) => new(
        UserRoot: root,
        ProjectsRoot: Path.Combine(root, "Projects"),
        VaultRoot: Path.Combine(root, "Vault"),
        BrowserProfilesRoot: Path.Combine(root, "BrowserProfiles"),
        EngineRoot: Path.Combine(root, "Engine"),
        UpdatesRoot: Path.Combine(root, "Updates"),
        LogsRoot: Path.Combine(root, "Logs"),
        CacheRoot: Path.Combine(root, "Cache"));
}
