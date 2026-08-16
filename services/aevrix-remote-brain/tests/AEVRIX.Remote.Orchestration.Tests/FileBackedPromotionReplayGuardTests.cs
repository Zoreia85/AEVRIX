using Aevrix.Remote.Orchestration;

#pragma warning disable CS0618 // Intentional legacy-compatibility coverage; production callers must use KeyedPromotionReplayGuard.

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class FileBackedPromotionReplayGuardTests
{
    [TestMethod]
    public void LegacyGuard_IsExplicitlyDeprecatedInFavorOfKeyedClaims()
    {
        var legacyType = typeof(IPromotionReplayGuard).Assembly.GetType(
            "Aevrix.Remote.Orchestration.FileBackedPromotionReplayGuard",
            throwOnError: true)!;
        var obsolete = legacyType
            .GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false)
            .Cast<ObsoleteAttribute>()
            .Single();

        StringAssert.Contains(obsolete.Message, nameof(KeyedPromotionReplayGuard));
        StringAssert.Contains(obsolete.Message, "256-bit");
    }

    [TestMethod]
    public void TryClaim_SurvivesGuardRecreation()
    {
        using var temp = new TemporaryDirectory();
        var attestation = Attestation();

        var first = new FileBackedPromotionReplayGuard(temp.Path);
        Assert.IsTrue(first.TryClaim(attestation, out var firstReplayKey));

        var recreated = new FileBackedPromotionReplayGuard(temp.Path);
        Assert.IsFalse(recreated.TryClaim(attestation, out var secondReplayKey));
        Assert.AreEqual(firstReplayKey, secondReplayKey);
        Assert.AreEqual(1, Directory.GetFiles(temp.Path, "*.claim").Length);
    }

    [TestMethod]
    public void TryClaim_ReSignedIdentityWithDifferentNonce_IsStillRejected()
    {
        using var temp = new TemporaryDirectory();
        var guard = new FileBackedPromotionReplayGuard(temp.Path);
        var first = Attestation("0123456789abcdef0123456789abcdef");
        var resigned = Attestation("fedcba9876543210fedcba9876543210");

        Assert.IsTrue(guard.TryClaim(first, out var firstReplayKey));
        Assert.IsFalse(guard.TryClaim(resigned, out var secondReplayKey));
        Assert.AreEqual(firstReplayKey, secondReplayKey);
    }

    [TestMethod]
    public void TryClaim_PersistsOnlyOpaqueClaimIdentifier()
    {
        using var temp = new TemporaryDirectory();
        var guard = new FileBackedPromotionReplayGuard(temp.Path);
        var attestation = Attestation();

        Assert.IsTrue(guard.TryClaim(attestation, out _));

        var file = AssertExactlyOneClaim(temp.Path);
        var fileName = Path.GetFileNameWithoutExtension(file);
        Assert.AreEqual(64, fileName.Length);
        Assert.IsTrue(fileName.All(static value => char.IsAsciiHexDigit(value)));
        Assert.IsFalse(Path.GetFileName(file).Contains(attestation.RunId, StringComparison.Ordinal));
        Assert.IsFalse(Path.GetFileName(file).Contains(attestation.ExecutionId, StringComparison.Ordinal));
        Assert.IsFalse(File.ReadAllText(file).Contains(attestation.RunId, StringComparison.Ordinal));
        Assert.IsFalse(File.ReadAllText(file).Contains(attestation.ExecutionId, StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryClaim_DifferentLedgerHead_CreatesIndependentClaim()
    {
        using var temp = new TemporaryDirectory();
        var guard = new FileBackedPromotionReplayGuard(temp.Path);
        var first = Attestation();
        var advanced = first with { LedgerHead = new ExecutionProofHead(8, H('f')) };

        Assert.IsTrue(guard.TryClaim(first, out _));
        Assert.IsTrue(guard.TryClaim(advanced, out _));
        Assert.AreEqual(2, Directory.GetFiles(temp.Path, "*.claim").Length);
    }

    private static string AssertExactlyOneClaim(string root)
    {
        var files = Directory.GetFiles(root, "*.claim");
        Assert.AreEqual(1, files.Length);
        return files[0];
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

    private static string H(char value) => new(value, 64);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-promotion-claims-" + Guid.NewGuid().ToString("N"));
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

#pragma warning restore CS0618
