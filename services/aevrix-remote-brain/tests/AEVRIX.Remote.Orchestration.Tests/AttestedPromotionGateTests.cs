using System.Security.Cryptography;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AttestedPromotionGateTests
{
    private static readonly Guid Project = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 19, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task PromoteAsync_ValidAttestation_InvokesSinkOnceWithVerifiedReceipt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var sink = new RecordingSink();
        var gate = Gate(key, sink);

        var receipt = await gate.PromoteAsync(new(Sign(key, evidence), evidence));

        Assert.AreEqual(1, sink.CallCount);
        Assert.AreEqual(evidence.ComputeDigestSha256(), receipt.Authority.EvidenceDigestSha256);
        Assert.AreEqual(Project, receipt.Authority.ProjectId);
        Assert.IsTrue(receipt.ReplayKey.Contains(Project.ToString("D"), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PromoteAsync_TamperedEvidence_NeverInvokesSink()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var attestation = Sign(key, evidence);
        var sink = new RecordingSink();
        var gate = Gate(key, sink);
        var tampered = evidence with { ValidationDigestSha256 = H('9') };

        await ExpectAsync<InvalidDataException>(() => gate.PromoteAsync(new(attestation, tampered)));

        Assert.AreEqual(0, sink.CallCount);
    }

    [TestMethod]
    public async Task PromoteAsync_SamePromotionTwice_IsRejectedBeforeSecondSinkCall()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var attestation = Sign(key, evidence);
        var sink = new RecordingSink();
        var gate = Gate(key, sink);

        await gate.PromoteAsync(new(attestation, evidence));
        await ExpectAsync<InvalidOperationException>(() => gate.PromoteAsync(new(attestation, evidence)));

        Assert.AreEqual(1, sink.CallCount);
    }

    [TestMethod]
    public async Task PromoteAsync_ReSignedSameEvidence_IsStillReplayRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var sink = new RecordingSink();
        var gate = Gate(key, sink);
        var first = Sign(key, evidence, "0123456789abcdef0123456789abcdef");
        var second = Sign(key, evidence, "fedcba9876543210fedcba9876543210");

        await gate.PromoteAsync(new(first, evidence));
        await ExpectAsync<InvalidOperationException>(() => gate.PromoteAsync(new(second, evidence)));

        Assert.AreEqual(1, sink.CallCount);
    }

    [TestMethod]
    public async Task PromoteAsync_SinkFailure_KeepsClaimToAvoidAmbiguousAutomaticRetry()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        var sink = new RecordingSink { Fail = true };
        var gate = Gate(key, sink);
        var attestation = Sign(key, evidence);

        await ExpectAsync<IOException>(() => gate.PromoteAsync(new(attestation, evidence)));
        sink.Fail = false;
        await ExpectAsync<InvalidOperationException>(() => gate.PromoteAsync(new(attestation, evidence)));

        Assert.AreEqual(1, sink.CallCount);
    }

    private static AttestedPromotionGate Gate(ECDsa key, RecordingSink sink)
    {
        var verifier = new PromotionAuthorityAttestationVerifier(
            PromotionAuthorityVerifierOptions.CreateDefault(
                "authority-test-key",
                key.ExportSubjectPublicKeyInfoPem()),
            new FixedTimeProvider(Now));
        return new AttestedPromotionGate(verifier, new InMemoryPromotionReplayGuard(), sink);
    }

    private static PromotionEvidenceEnvelope Evidence() =>
        new(
            ExecutionProofLedger.CurrentVersion,
            Project,
            "run-promotion-gate",
            "exec-promotion-gate",
            "generic-analysis",
            "adapter-neutral",
            H('a'),
            H('b'),
            H('c'),
            H('d'),
            H('e'),
            new ExecutionProofHead(7, H('e')));

    private static PromotionAuthorityAttestation Sign(
        ECDsa signingKey,
        PromotionEvidenceEnvelope evidence,
        string nonce = "0123456789abcdef0123456789abcdef")
    {
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(signingKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var unsigned = new PromotionAuthorityAttestation(
            PromotionAuthorityAttestation.CurrentVersion,
            "authority-test-key",
            evidence.ProjectId,
            evidence.RunId,
            evidence.ExecutionId,
            evidence.ComputeDigestSha256(),
            evidence.LedgerHead.EntryCount,
            evidence.LedgerHead.HeadHashSha256,
            Now.AddMinutes(-1).ToUnixTimeSeconds(),
            Now.AddMinutes(4).ToUnixTimeSeconds(),
            nonce,
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

    private static async Task ExpectAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
            Assert.Fail($"Expected {typeof(TException).Name} was not thrown.");
        }
        catch (TException)
        {
        }
    }

    private static string H(char value) => new(value, 64);

    private sealed class RecordingSink : IAttestedPromotionSink
    {
        public int CallCount { get; private set; }
        public bool Fail { get; set; }

        public Task PromoteAsync(
            AttestedPromotionReceipt receipt,
            PromotionEvidenceEnvelope evidence,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (Fail) throw new IOException("simulated ambiguous promotion failure");
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
