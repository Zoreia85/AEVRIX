using System.Security.Cryptography;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class KeyedPromotionReplayGuardTests
{
    [TestMethod]
    public void TryClaim_ReSignedIdentityWithDifferentNonce_IsRejectedAcrossGuardRecreation()
    {
        using var temp = new TemporaryDirectory();
        var key = Key(0x2a);
        var store = new FilePromotionClaimStore(temp.Path);

        using (var firstGuard = new KeyedPromotionReplayGuard(store, key))
        {
            Assert.IsTrue(firstGuard.TryClaim(Attestation("0123456789abcdef0123456789abcdef"), out var firstClaimId));
            Assert.AreEqual(64, firstClaimId.Length);
        }

        using var recreated = new KeyedPromotionReplayGuard(new FilePromotionClaimStore(temp.Path), key);
        Assert.IsFalse(recreated.TryClaim(Attestation("fedcba9876543210fedcba9876543210"), out var secondClaimId));
        Assert.AreEqual(1, Directory.GetFiles(temp.Path, "*.claim").Length);
        Assert.AreEqual(Path.GetFileNameWithoutExtension(Directory.GetFiles(temp.Path, "*.claim")[0]), secondClaimId);
    }

    [TestMethod]
    public void TryClaim_PersistsNoPlaintextPromotionIdentifiers()
    {
        using var temp = new TemporaryDirectory();
        var attestation = Attestation();
        using var guard = new KeyedPromotionReplayGuard(new FilePromotionClaimStore(temp.Path), Key(0x31));

        Assert.IsTrue(guard.TryClaim(attestation, out var claimId));

        var file = AssertExactlyOneClaim(temp.Path);
        var persisted = Path.GetFileName(file) + "\n" + File.ReadAllText(file);
        Assert.AreEqual(claimId, Path.GetFileNameWithoutExtension(file));
        Assert.IsFalse(persisted.Contains(attestation.ProjectId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(persisted.Contains(attestation.RunId, StringComparison.Ordinal));
        Assert.IsFalse(persisted.Contains(attestation.ExecutionId, StringComparison.Ordinal));
        Assert.IsFalse(persisted.Contains(attestation.EvidenceDigestSha256, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(persisted.Contains(attestation.LedgerHead.HeadHashSha256, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TryClaim_DifferentDeploymentKeys_ProduceUnlinkableClaimIdentifiers()
    {
        var attestation = Attestation();
        var firstStore = new RecordingClaimStore();
        var secondStore = new RecordingClaimStore();
        using var first = new KeyedPromotionReplayGuard(firstStore, Key(0x11));
        using var second = new KeyedPromotionReplayGuard(secondStore, Key(0x22));

        Assert.IsTrue(first.TryClaim(attestation, out var firstClaimId));
        Assert.IsTrue(second.TryClaim(attestation, out var secondClaimId));

        Assert.AreNotEqual(firstClaimId, secondClaimId);
        Assert.AreEqual(firstClaimId, firstStore.LastClaimId);
        Assert.AreEqual(secondClaimId, secondStore.LastClaimId);
    }

    [TestMethod]
    public void Constructor_RejectsWeakHmacKey()
    {
        var store = new RecordingClaimStore();
        var weakKey = new byte[31];

        Assert.ThrowsExactly<ArgumentException>(() => new KeyedPromotionReplayGuard(store, weakKey));
    }

    [TestMethod]
    public void FileStore_ConcurrentClaim_AllowsExactlyOneWinner()
    {
        using var temp = new TemporaryDirectory();
        var store = new FilePromotionClaimStore(temp.Path);
        var claimId = new string('a', 64);
        var results = new bool[32];

        Parallel.For(0, results.Length, index => results[index] = store.TryCreate(claimId));

        Assert.AreEqual(1, results.Count(static value => value));
        Assert.AreEqual(1, Directory.GetFiles(temp.Path, "*.claim").Length);
    }

    [TestMethod]
    public void FileStore_AtomicClaimSet_RejectsSymlinkLockFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TemporaryDirectory();
        var target = Path.Combine(temp.Path, "lock-target");
        File.WriteAllText(target, "target");
        var lockPath = Path.Combine(temp.Path, ".aevrix-promotion-claim-set.lock");
        File.CreateSymbolicLink(lockPath, target);
        var store = new FilePromotionClaimStore(temp.Path);

        Assert.ThrowsExactly<IOException>(() =>
            store.TryCreateIfNoneExist(new string('c', 64), Array.Empty<string>()));
        Assert.AreEqual("target", File.ReadAllText(target));
    }

    [TestMethod]
    public void Dispose_FailsClosedForFurtherClaims()
    {
        var guard = new KeyedPromotionReplayGuard(new RecordingClaimStore(), Key(0x44));
        guard.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => guard.TryClaim(Attestation(), out _));
    }

    private static VerifiedPromotionAuthorityAttestation Attestation(
        string nonce = "0123456789abcdef0123456789abcdef") =>
        new(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            "run-private-customer-42",
            "exec-private-customer-42",
            H('a'),
            new ExecutionProofHead(7, H('e')),
            "authority-test-key",
            1_787_000_000,
            1_787_000_300,
            nonce,
            H('b'));

    private static byte[] Key(byte value)
    {
        var key = new byte[32];
        Array.Fill(key, value);
        return key;
    }

    private static string H(char value) => new(value, 64);

    private static string AssertExactlyOneClaim(string root)
    {
        var files = Directory.GetFiles(root, "*.claim");
        Assert.AreEqual(1, files.Length);
        return files[0];
    }

    private sealed class RecordingClaimStore : IPromotionClaimStore
    {
        public string? LastClaimId { get; private set; }

        public bool TryCreate(string opaqueClaimId)
        {
            LastClaimId = opaqueClaimId;
            return true;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-keyed-promotion-claims-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not hide the assertion result.
            }
        }
    }
}
