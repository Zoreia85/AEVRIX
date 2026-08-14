using System.Security.Cryptography;
using System.Text;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class EncryptedQirObservationStoreTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string FeatureHash = new('a', 64);

    [TestMethod]
    public async Task StoreRoundTripEncryptsProjectMaterialAtRest()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedQirObservationStore(temp.Path, new DeterministicKeyProvider());
        var observation = Obs(A, "obs-alpha", "runtime.framework", "ev-sensitive-alpha");

        await store.StoreAsync(observation);
        var loaded = (await store.LoadProjectAsync(A)).Single();

        Assert.AreEqual(observation, loaded);
        var bytes = File.ReadAllBytes(Directory.EnumerateFiles(temp.Path, "*.qir", SearchOption.AllDirectories).Single());
        var text = Encoding.UTF8.GetString(bytes);
        Assert.IsFalse(text.Contains("runtime.framework", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("ev-sensitive-alpha", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DifferentProjectKeyCannotDecryptStoredObservation()
    {
        using var temp = new TempDirectory();
        var good = new EncryptedQirObservationStore(temp.Path, new DeterministicKeyProvider());
        await good.StoreAsync(Obs(A, "obs-alpha", "runtime.framework", "ev-alpha"));

        var wrong = new EncryptedQirObservationStore(temp.Path, new FixedKeyProvider(0x7f));
        await Assert.ThrowsAsync<InvalidDataException>(() => wrong.LoadProjectAsync(A));
    }

    [TestMethod]
    public async Task SameObservationIdRemainsIndependentAcrossProjects()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedQirObservationStore(temp.Path, new DeterministicKeyProvider());
        await store.StoreAsync(Obs(A, "obs-shared", "runtime.framework", "ev-a"));
        await store.StoreAsync(Obs(B, "obs-shared", "binary.format", "ev-b"));

        Assert.AreEqual("runtime.framework", (await store.LoadProjectAsync(A)).Single().PatternKey);
        Assert.AreEqual("binary.format", (await store.LoadProjectAsync(B)).Single().PatternKey);
    }

    [TestMethod]
    public async Task PersistentLedgerHydratesIndependentProjectsBeforePromotion()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedQirObservationStore(temp.Path, new DeterministicKeyProvider());
        var first = new PersistentQirLearningLedger(store, timeProvider: new FixedTimeProvider());
        await first.RecordAsync(Obs(A, "obs-a", "runtime.framework", "ev-a"));
        await first.RecordAsync(Obs(B, "obs-b", "runtime.framework", "ev-b"));

        var restored = new PersistentQirLearningLedger(store, timeProvider: new FixedTimeProvider());
        Assert.AreEqual(1, await restored.HydrateProjectAsync(A));
        Assert.Throws<InvalidOperationException>(() => restored.Promote("runtime.framework", FeatureHash));
        Assert.AreEqual(1, await restored.HydrateProjectAsync(B));

        var promoted = restored.Promote("runtime.framework", FeatureHash);
        Assert.AreEqual(2, promoted.IndependentProjectCount);
        Assert.AreEqual(2, promoted.ObservationCount);
    }

    [TestMethod]
    public async Task PersistentObservationIdCannotBeRebound()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedQirObservationStore(temp.Path, new DeterministicKeyProvider());
        var first = Obs(A, "obs-fixed", "runtime.framework", "ev-a");
        await store.StoreAsync(first);
        await store.StoreAsync(first);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.StoreAsync(first with { Confidence = 0.81 }));
    }

    private static QirLearningObservation Obs(Guid project, string id, string key, string evidenceId) =>
        new(id, project, key, FeatureHash, QirLearningBasis.Observed, QirLearningSensitivity.Public,
            0.95, [evidenceId], new DateTimeOffset(2026, 8, 14, 23, 0, 0, TimeSpan.Zero));

    private sealed class DeterministicKeyProvider : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(SHA256.HashData(projectId.ToByteArray()));
        }
    }

    private sealed class FixedKeyProvider(byte value) : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(Enumerable.Repeat(value, 32).Select(x => (byte)x).ToArray());
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 14, 23, 1, 0, TimeSpan.Zero);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-qir-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
