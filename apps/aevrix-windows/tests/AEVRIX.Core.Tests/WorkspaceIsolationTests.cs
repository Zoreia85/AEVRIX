using System.Security.Cryptography;
using System.Text;
using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class WorkspaceIsolationTests
{
    [TestMethod]
    public void WorkspacePaths_AreOpaqueAndSeparatedAcrossUsersAndWorkspaces()
    {
        using var temp = new TemporaryDirectory();
        var root = PathsFor(temp.Path);

        var a = new WorkspaceDataPaths(root, new WorkspaceScope("workspace-alpha", "user-a", "enc-a"));
        var b = new WorkspaceDataPaths(root, new WorkspaceScope("workspace-alpha", "user-b", "enc-a"));
        var c = new WorkspaceDataPaths(root, new WorkspaceScope("workspace-beta", "user-a", "enc-a"));

        Assert.AreNotEqual(a.WorkspaceRoot, b.WorkspaceRoot);
        Assert.AreNotEqual(a.WorkspaceRoot, c.WorkspaceRoot);
        StringAssert.DoesNotContain(a.WorkspaceRoot, "user-a");
        StringAssert.DoesNotContain(a.WorkspaceRoot, "workspace-alpha");
        Assert.IsTrue(a.Contains(a.ProjectRoot(Guid.NewGuid())));
        Assert.IsFalse(a.Contains(b.WorkspaceRoot));
    }

    [TestMethod]
    public void ResolveWorkspaceRelativePath_RejectsTraversalAndRootedPaths()
    {
        using var temp = new TemporaryDirectory();
        var workspace = new WorkspaceDataPaths(PathsFor(temp.Path), new WorkspaceScope("ws", "user", "enc"));

        Assert.Throws<InvalidOperationException>(() => workspace.ResolveWorkspaceRelativePath("..\\escape.txt"));
        Assert.Throws<InvalidOperationException>(() => workspace.ResolveWorkspaceRelativePath(Path.GetFullPath(Path.Combine(temp.Path, "absolute.txt"))));
    }

    [TestMethod]
    public void EnvelopeEncryption_RoundTripsOnlyInsideSameWorkspaceAndPurpose()
    {
        using var temp = new TemporaryDirectory();
        var root = PathsFor(temp.Path);
        var scopeA = new WorkspaceDataPaths(root, new WorkspaceScope("ws-a", "user-a", "enc-a"));
        var scopeB = new WorkspaceDataPaths(root, new WorkspaceScope("ws-b", "user-a", "enc-a"));
        var cryptoA = new WorkspaceEnvelopeEncryption(scopeA);
        var cryptoB = new WorkspaceEnvelopeEncryption(scopeB);
        var masterKey = SHA256.HashData(Encoding.UTF8.GetBytes("test-only-master-key-material"));
        var plaintext = Encoding.UTF8.GetBytes("private evidence payload");

        var envelope = cryptoA.Encrypt(plaintext, masterKey, "evidence");
        var restored = cryptoA.Decrypt(envelope, masterKey, "evidence");

        CollectionAssert.AreEqual(plaintext, restored);
        Assert.Throws<CryptographicException>(() => cryptoB.Decrypt(envelope, masterKey, "evidence"));
        Assert.Throws<CryptographicException>(() => cryptoA.Decrypt(envelope, masterKey, "blueprint"));
        CollectionAssert.AreNotEqual(plaintext, envelope.Ciphertext);
    }

    [TestMethod]
    public void EnvelopeEncryption_RejectsShortMasterKeysAndTampering()
    {
        using var temp = new TemporaryDirectory();
        var workspace = new WorkspaceDataPaths(PathsFor(temp.Path), new WorkspaceScope("ws", "user", "enc"));
        var crypto = new WorkspaceEnvelopeEncryption(workspace);
        var plaintext = Encoding.UTF8.GetBytes("payload");
        var masterKey = RandomNumberGenerator.GetBytes(32);

        Assert.Throws<ArgumentException>(() => crypto.Encrypt(plaintext, new byte[31], "evidence"));

        var envelope = crypto.Encrypt(plaintext, masterKey, "evidence");
        envelope.Ciphertext[0] ^= 0x01;
        Assert.Throws<CryptographicException>(() => crypto.Decrypt(envelope, masterKey, "evidence"));
    }

    private static AevrixDataPaths PathsFor(string root) => new(
        UserRoot: root,
        ProjectsRoot: Path.Combine(root, "Projects"),
        VaultRoot: Path.Combine(root, "Vault"),
        BrowserProfilesRoot: Path.Combine(root, "BrowserProfiles"),
        EngineRoot: Path.Combine(root, "Engine"),
        UpdatesRoot: Path.Combine(root, "Updates"),
        LogsRoot: Path.Combine(root, "Logs"),
        CacheRoot: Path.Combine(root, "Cache"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-workspace-isolation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
