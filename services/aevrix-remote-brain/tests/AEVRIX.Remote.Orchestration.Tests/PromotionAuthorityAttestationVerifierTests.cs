using System.Security.Cryptography;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class PromotionAuthorityAttestationVerifierTests
{
    private static readonly Guid Project = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 18, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Verify_ValidAttestation_ReturnsBoundReceiptWithoutAuthorityCredential()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var attestation = Sign(key, evidence, Now.AddMinutes(-1), Now.AddMinutes(4));
        var verifier = Verifier(key);

        var verified = verifier.Verify(attestation, evidence);

        Assert.AreEqual(Project, verified.ProjectId);
        Assert.AreEqual(evidence.ComputeDigestSha256(), verified.EvidenceDigestSha256);
        Assert.AreEqual(evidence.LedgerHead.EntryCount, verified.LedgerHead.EntryCount);
        Assert.AreEqual(evidence.LedgerHead.HeadHashSha256, verified.LedgerHead.HeadHashSha256);
        Assert.AreEqual("authority-test-key", verified.KeyId);
    }

    [TestMethod]
    public void Verify_TamperedEvidence_IsRejectedEvenWhenSignatureItselfIsValid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var attestation = Sign(key, evidence, Now.AddMinutes(-1), Now.AddMinutes(4));
        var tampered = evidence with { PromotionDigestSha256 = H('9') };

        Assert.ThrowsException<InvalidDataException>(() => Verifier(key).Verify(attestation, tampered));
    }

    [TestMethod]
    public void Verify_ForgedSignature_IsRejected()
    {
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var forged = Sign(attackerKey, evidence, Now.AddMinutes(-1), Now.AddMinutes(4),
            advertisedFingerprintKey: trustedKey);

        Assert.ThrowsException<InvalidDataException>(() => Verifier(trustedKey).Verify(forged, evidence));
    }

    [TestMethod]
    public void Verify_WrongPinnedKeyFingerprint_IsRejectedBeforePromotion()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherPinnedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var attestation = Sign(signingKey, evidence, Now.AddMinutes(-1), Now.AddMinutes(4));

        Assert.ThrowsException<InvalidDataException>(() => Verifier(otherPinnedKey).Verify(attestation, evidence));
    }

    [TestMethod]
    public void Verify_ExpiredFutureDatedAndOverlongAttestations_AreRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var verifier = Verifier(key);

        var expired = Sign(key, evidence, Now.AddMinutes(-5), Now.AddSeconds(-1));
        var future = Sign(key, evidence, Now.AddMinutes(1), Now.AddMinutes(2));
        var overlong = Sign(key, evidence, Now.AddMinutes(-1), Now.AddMinutes(20));

        Assert.ThrowsException<InvalidDataException>(() => verifier.Verify(expired, evidence));
        Assert.ThrowsException<InvalidDataException>(() => verifier.Verify(future, evidence));
        Assert.ThrowsException<InvalidDataException>(() => verifier.Verify(overlong, evidence));
    }

    [TestMethod]
    public void Verify_AttestationForAnotherProjectOrHead_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var attestation = Sign(key, evidence, Now.AddMinutes(-1), Now.AddMinutes(4));
        var verifier = Verifier(key);

        Assert.ThrowsException<InvalidDataException>(() => verifier.Verify(
            attestation with { ProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333") },
            evidence));
        Assert.ThrowsException<InvalidDataException>(() => verifier.Verify(
            attestation with { HeadEntryCount = evidence.LedgerHead.EntryCount + 1 },
            evidence));
    }

    [TestMethod]
    public void Verify_RejectsEvidenceWhereAuthorizationIsNotCurrentAnchoredHead()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var attestation = Sign(key, evidence, Now.AddMinutes(-1), Now.AddMinutes(4));
        var staleAuthorization = evidence with { AuthorizationRecordHashSha256 = H('8') };

        Assert.ThrowsException<InvalidDataException>(() => Verifier(key).Verify(attestation, staleAuthorization));
    }

    private static PromotionAuthorityAttestationVerifier Verifier(ECDsa key)
    {
        var options = PromotionAuthorityVerifierOptions.CreateDefault(
            "authority-test-key",
            key.ExportSubjectPublicKeyInfoPem());
        return new PromotionAuthorityAttestationVerifier(options, new FixedTimeProvider(Now));
    }

    private static PromotionEvidenceEnvelope Evidence() =>
        new(
            ExecutionProofLedger.CurrentVersion,
            Project,
            "run-verifier",
            "exec-verifier",
            "generic-analysis",
            "adapter-neutral",
            H('a'),
            H('b'),
            H('c'),
            H('d'),
            H('e'),
            new ExecutionProofHead(5, H('e')));

    private static PromotionAuthorityAttestation Sign(
        ECDsa signingKey,
        PromotionEvidenceEnvelope evidence,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        ECDsa? advertisedFingerprintKey = null)
    {
        var fingerprintKey = advertisedFingerprintKey ?? signingKey;
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(fingerprintKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var unsigned = new PromotionAuthorityAttestation(
            PromotionAuthorityAttestation.CurrentVersion,
            "authority-test-key",
            evidence.ProjectId,
            evidence.RunId,
            evidence.ExecutionId,
            evidence.ComputeDigestSha256(),
            evidence.LedgerHead.EntryCount,
            evidence.LedgerHead.HeadHashSha256,
            issuedAt.ToUnixTimeSeconds(),
            expiresAt.ToUnixTimeSeconds(),
            "0123456789abcdef0123456789abcdef",
            "AA==",
            fingerprint);

        var payload = unsigned.CanonicalPayloadUtf8();
        var signature = signingKey.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        try
        {
            return unsigned with { SignatureDerBase64 = Convert.ToBase64String(signature) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static string H(char value) => new(value, 64);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
