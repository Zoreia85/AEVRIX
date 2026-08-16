using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class RotatingKeyedPromotionReplayGuardTests
{
    [TestMethod]
    public void TryClaim_PreRotationClaim_IsRejectedAfterRotation()
    {
        using var temp = new TemporaryDirectory();
        var oldKey = Key(0x11);
        var newKey = Key(0x22);
        var store = new FilePromotionClaimStore(temp.Path);
        var attestation = Attestation();

        using (var oldGuard = new KeyedPromotionReplayGuard(store, oldKey))
            Assert.IsTrue(oldGuard.TryClaim(attestation, out _));

        using var rotated = new RotatingKeyedPromotionReplayGuard(store, newKey, [oldKey]);
        Assert.IsFalse(rotated.TryClaim(attestation, out _));
        Assert.AreEqual(1, Directory.GetFiles(temp.Path, "*.claim").Length);
    }

    [TestMethod]
    public void TryClaim_NewPromotion_PersistsOnlyCurrentAliasAndNoPlaintext()
    {
        using var temp = new TemporaryDirectory();
        var attestation = Attestation();
        var store = new FilePromotionClaimStore(temp.Path);
        using var guard = new RotatingKeyedPromotionReplayGuard(store, Key(0x31), [Key(0x32)]);

        Assert.IsTrue(guard.TryClaim(attestation, out var claimId));

        var file = Directory.GetFiles(temp.Path, "*.claim").Single();
        var persisted = Path.GetFileName(file) + "\n" + File.ReadAllText(file);
        Assert.AreEqual(claimId, Path.GetFileNameWithoutExtension(file));
        Assert.AreEqual(1, Directory.GetFiles(temp.Path, "*.claim").Length);
        Assert.IsFalse(persisted.Contains(attestation.ProjectId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(persisted.Contains(attestation.RunId, StringComparison.Ordinal));
        Assert.IsFalse(persisted.Contains(attestation.ExecutionId, StringComparison.Ordinal));
        Assert.IsFalse(persisted.Contains(attestation.EvidenceDigestSha256, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Constructor_RejectsWeakPreviousKey()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new RotatingKeyedPromotionReplayGuard(
                new MemoryLookupStore(),
                Key(0x41),
                [new byte[31]]));
    }

    [TestMethod]
    public void Dispose_FailsClosed()
    {
        var guard = new RotatingKeyedPromotionReplayGuard(
            new MemoryLookupStore(),
            Key(0x51),
            [Key(0x52)]);
        guard.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => guard.TryClaim(Attestation(), out _));
    }

    private static VerifiedPromotionAuthorityAttestation Attestation() =>
        new(
            Guid.Parse("65656565-6565-6565-6565-656565656565"),
            "run-rotation-private",
            "exec-rotation-private",
            H('a'),
            new ExecutionProofHead(9, H('e')),
            "authority-rotation-key",
            1_787_000_000,
            1_787_000_300,
            "0123456789abcdef0123456789abcdef",
            H('b'));

    private static byte[] Key(byte value)
    {
        var key = new byte[32];
        Array.Fill(key, value);
        return key;
    }

    private static string H(char value) => new(value, 64);

    private sealed class MemoryLookupStore : IAtomicPromotionClaimSetStore
    {
        private readonly HashSet<string> _claims = new(StringComparer.Ordinal);
        public bool TryCreate(string opaqueClaimId) => _claims.Add(opaqueClaimId);
        public bool Exists(string opaqueClaimId) => _claims.Contains(opaqueClaimId);

        public bool TryCreateIfNoneExist(
            string opaqueClaimId,
            IReadOnlyCollection<string> forbiddenClaimIds)
        {
            if (_claims.Contains(opaqueClaimId) || forbiddenClaimIds.Any(_claims.Contains))
                return false;

            return _claims.Add(opaqueClaimId);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-rotating-claims-" + Guid.NewGuid().ToString("N"));
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
                // Cleanup must not hide assertion failures.
            }
        }
    }
}
