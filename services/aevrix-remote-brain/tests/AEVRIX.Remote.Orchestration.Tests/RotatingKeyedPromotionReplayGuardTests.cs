using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class RotatingKeyedPromotionReplayGuardTests
{
    [TestMethod]
    public void TryClaim_PreRotationClaim_IsRejectedAfterKeyRotation()
    {
        using var temp = new TemporaryDirectory();
        var oldKey = Key(0x11);
        var newKey = Key(0x22);
        var attestation = Attestation();

        using (var oldGuard = new KeyedPromotionReplayGuard(new FilePromotionClaimStore(temp.Path), oldKey))
        {
            Assert.IsTrue(oldGuard.TryClaim(attestation, out var oldClaimId));
            Assert.AreEqual(64, oldClaimId.Length);
        }

        using var rotated = new RotatingKeyedPromotionReplayGuard(
            new FilePromotionClaimStore(temp.Path),
            newKey,
            new[] { oldKey });

        Assert.IsFalse(rotated.TryClaim(attestation, out var replayKey));
        Assert.AreEqual(1, Directory.GetFiles(temp.Path, "*.claim").Length);
        Assert.AreEqual(Path.GetFileNameWithoutExtension(Directory.GetFiles(temp.Path, "*.claim")[0]), replayKey);
    }

    [TestMethod]
    public void TryClaim_NewIdentity_WritesOnlyCurrentKeyAlias()
    {
        using var temp = new TemporaryDirectory();
        var oldKey = Key(0x31);
        var newKey = Key(0x32);
        var attestation = Attestation();
        var store = new FilePromotionClaimStore(temp.Path);

        string expectedCurrent;
        using (var current = new KeyedPromotionReplayGuard(new RecordingClaimStore(), newKey))
        {
            Assert.IsTrue(current.TryClaim(attestation, out expectedCurrent));
        }

        using var rotated = new RotatingKeyedPromotionReplayGuard(store, newKey, new[] { oldKey });
        Assert.IsTrue(rotated.TryClaim(attestation, out var replayKey));

        Assert.AreEqual(expectedCurrent, replayKey);
        var claims = Directory.GetFiles(temp.Path, "*.claim");
        Assert.AreEqual(1, claims.Length);
        Assert.AreEqual(expectedCurrent, Path.GetFileNameWithoutExtension(claims[0]));
    }

    [TestMethod]
    public void TryClaim_RotationStorageContainsNoPlaintextPromotionIdentifiers()
    {
        using var temp = new TemporaryDirectory();
        var attestation = Attestation();
        using var rotated = new RotatingKeyedPromotionReplayGuard(
            new FilePromotionClaimStore(temp.Path),
            Key(0x41),
            new[] { Key(0x40) });

        Assert.IsTrue(rotated.TryClaim(attestation, out _));

        var file = Directory.GetFiles(temp.Path, "*.claim").Single();
        var persisted = Path.GetFileName(file) + "\n" + File.ReadAllText(file);
        Assert.IsFalse(persisted.Contains(attestation.ProjectId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(persisted.Contains(attestation.RunId, StringComparison.Ordinal));
        Assert.IsFalse(persisted.Contains(attestation.ExecutionId, StringComparison.Ordinal));
        Assert.IsFalse(persisted.Contains(attestation.EvidenceDigestSha256, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(persisted.Contains(attestation.LedgerHead.HeadHashSha256, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Constructor_RejectsWeakLegacyKey()
    {
        using var temp = new TemporaryDirectory();
        var weakLegacy = new byte[31];

        Assert.ThrowsExactly<ArgumentException>(() => new RotatingKeyedPromotionReplayGuard(
            new FilePromotionClaimStore(temp.Path),
            Key(0x51),
            new[] { weakLegacy }));
    }

    [TestMethod]
    public void Dispose_FailsClosedForFurtherClaims()
    {
        using var temp = new TemporaryDirectory();
        var guard = new RotatingKeyedPromotionReplayGuard(
            new FilePromotionClaimStore(temp.Path),
            Key(0x61),
            new[] { Key(0x60) });
        guard.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => guard.TryClaim(Attestation(), out _));
    }

    private static VerifiedPromotionAuthorityAttestation Attestation() =>
        new(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            "run-private-rotation-42",
            "exec-private-rotation-42",
            H('a'),
            new ExecutionProofHead(9, H('e')),
            "authority-test-key",
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

    private sealed class RecordingClaimStore : IPromotionClaimStore
    {
        public bool TryCreate(string opaqueClaimId) => true;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-rotating-promotion-claims-" + Guid.NewGuid().ToString("N"));
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
