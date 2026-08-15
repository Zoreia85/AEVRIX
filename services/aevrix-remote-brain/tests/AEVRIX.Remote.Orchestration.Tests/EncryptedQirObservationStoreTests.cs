using System.Security.Cryptography;
using System.Text;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class EncryptedQirObservationStoreTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [TestMethod]
    public async Task RoundTripIsEncryptedAndProjectBound()
    {
        using var temp = new TempDir();
        var store = new EncryptedQirObservationStore(temp.Path, new Keys());
        var item = Obs(A, "obs-a", "runtime.framework", "ev-sensitive");
        await store.StoreAsync(item);
        var loaded = (await store.LoadProjectAsync(A)).Single();
        Assert.AreEqual(item.ObservationId, loaded.ObservationId);
        Assert.AreEqual(item.PatternKey, loaded.PatternKey);
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(Directory.EnumerateFiles(temp.Path, "*.qir", SearchOption.AllDirectories).Single()));
        Assert.IsFalse(text.Contains("runtime.framework", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("ev-sensitive", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains(item.ProjectId.ToString("D"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SameObservationIdDoesNotLeakAcrossProjects()
    {
        using var temp = new TempDir();
        var store = new EncryptedQirObservationStore(temp.Path, new Keys());
        await store.StoreAsync(Obs(A, "obs-shared", "runtime.framework", "ev-a"));
        await store.StoreAsync(Obs(B, "obs-shared", "binary.format", "ev-b"));
        Assert.AreEqual("runtime.framework", (await store.LoadProjectAsync(A)).Single().PatternKey);
        Assert.AreEqual("binary.format", (await store.LoadProjectAsync(B)).Single().PatternKey);
    }

    [TestMethod]
    public async Task WrongKeyAndIdRebindingFailClosed()
    {
        using var temp = new TempDir();
        var store = new EncryptedQirObservationStore(temp.Path, new Keys());
        var item = Obs(A, "obs-fixed", "runtime.framework", "ev-a");
        await store.StoreAsync(item);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(item with { Confidence = .81 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new EncryptedQirObservationStore(temp.Path, new FixedKey()).LoadProjectAsync(A));
    }

    private static QirLearningObservation Obs(Guid project, string id, string pattern, string evidence) =>
        new(id, project, pattern, new string('a', 64), QirLearningBasis.Observed, QirLearningSensitivity.Public,
            .95, [evidence], new DateTimeOffset(2026, 8, 14, 23, 0, 0, TimeSpan.Zero));

    private sealed class Keys : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(SHA256.HashData(projectId.ToByteArray()));
    }

    private sealed class FixedKey : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(Enumerable.Repeat((byte)0x7f, 32).ToArray());
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-qir-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
