using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Remote.Security.Tests;

[TestClass]
public sealed class DpopProofValidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T04:00:00Z");

    [TestMethod]
    public async Task ValidProofBindsMethodUriTokenNonceBodyAndRegistersOnlyJtiHash()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var replay = new RecordingReplayStore();
        var nonce = new StaticNonceValidator("server-nonce-1234567890");
        var validator = new DpopProofValidator(replay, nonce);
        var token = "short-lived-access-token";
        var body = Encoding.UTF8.GetBytes("{\"captureId\":\"capture-1\"}");
        var uri = new Uri("https://api.aevrix.example:8443/v1/blueprint?project=secret#fragment");
        var proof = CreateProof(key, "POST", uri, token, body, nonce.Nonce, Now, "jti-value-1234567890");
        var expectedThumbprint = JwkThumbprint(key);

        var result = await validator.ValidateAsync(new DpopValidationInput(
            proof, "POST", uri, token, body, expectedThumbprint, nonce.Nonce, Now));

        Assert.IsTrue(result.Valid);
        Assert.AreEqual("ok", result.Code);
        Assert.AreEqual(expectedThumbprint, result.JwkThumbprint);
        Assert.IsNotNull(replay.LastDigest);
        Assert.AreEqual(32, replay.LastDigest.Length);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("jti-value-1234567890"))).ToLowerInvariant(),
            result.JtiSha256);
        Assert.AreEqual(TimeSpan.FromSeconds(120), replay.LastTtl);
    }

    [TestMethod]
    public async Task ReplayedProofIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var replay = new RecordingReplayStore();
        var nonce = new StaticNonceValidator("server-nonce-1234567890");
        var validator = new DpopProofValidator(replay, nonce);
        var token = "short-lived-access-token";
        var body = Encoding.UTF8.GetBytes("{}");
        var uri = new Uri("https://api.aevrix.example/v1/resource");
        var proof = CreateProof(key, "POST", uri, token, body, nonce.Nonce, Now, "jti-replay-1234567890");
        var input = new DpopValidationInput(proof, "POST", uri, token, body, JwkThumbprint(key), nonce.Nonce, Now);

        Assert.IsTrue((await validator.ValidateAsync(input)).Valid);
        var second = await validator.ValidateAsync(input);
        Assert.IsFalse(second.Valid);
        Assert.AreEqual("dpop_replay_detected", second.Code);
    }

    [TestMethod]
    public async Task ProofFailsClosedWhenAnyBoundInputChangesOrProofIsStale()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = "short-lived-access-token";
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var uri = new Uri("https://api.aevrix.example/v1/resource?visible=no");
        var nonceText = "server-nonce-1234567890";
        var proof = CreateProof(key, "POST", uri, token, body, nonceText, Now, "jti-bound-1234567890");
        var thumbprint = JwkThumbprint(key);

        async Task<string> Code(DpopValidationInput input)
        {
            var validator = new DpopProofValidator(new RecordingReplayStore(), new StaticNonceValidator(nonceText));
            return (await validator.ValidateAsync(input)).Code;
        }

        Assert.AreEqual("dpop_claim_binding_mismatch", await Code(new DpopValidationInput(proof, "GET", uri, token, body, thumbprint, nonceText, Now)));
        Assert.AreEqual("dpop_claim_binding_mismatch", await Code(new DpopValidationInput(proof, "POST", new Uri("https://api.aevrix.example/v1/other"), token, body, thumbprint, nonceText, Now)));
        Assert.AreEqual("dpop_claim_binding_mismatch", await Code(new DpopValidationInput(proof, "POST", uri, "different-token", body, thumbprint, nonceText, Now)));
        Assert.AreEqual("dpop_claim_binding_mismatch", await Code(new DpopValidationInput(proof, "POST", uri, token, Encoding.UTF8.GetBytes("{\"a\":2}"), thumbprint, nonceText, Now)));
        Assert.AreEqual("dpop_nonce_mismatch", await Code(new DpopValidationInput(proof, "POST", uri, token, body, thumbprint, "different-server-nonce-1234", Now)));
        Assert.AreEqual("dpop_proof_expired_or_future", await Code(new DpopValidationInput(proof, "POST", uri, token, body, thumbprint, nonceText, Now.AddSeconds(91))));
    }

    [TestMethod]
    public async Task ProofWithDifferentBoundPublicKeyIsRejectedBeforeReplayRegistration()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var expected = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var replay = new RecordingReplayStore();
        var nonce = new StaticNonceValidator("server-nonce-1234567890");
        var uri = new Uri("https://api.aevrix.example/v1/resource");
        var proof = CreateProof(signer, "GET", uri, "token-value", [], nonce.Nonce, Now, "jti-key-1234567890");
        var validator = new DpopProofValidator(replay, nonce);

        var result = await validator.ValidateAsync(new DpopValidationInput(
            proof, "GET", uri, "token-value", [], JwkThumbprint(expected), nonce.Nonce, Now));

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("dpop_key_binding_mismatch", result.Code);
        Assert.IsNull(replay.LastDigest);
    }

    private static string CreateProof(
        ECDsa key,
        string method,
        Uri uri,
        string token,
        ReadOnlySpan<byte> body,
        string nonce,
        DateTimeOffset issuedAt,
        string jti)
    {
        var parameters = key.ExportParameters(false);
        var x = Base64Url(parameters.Q.X!);
        var y = Base64Url(parameters.Q.Y!);
        var header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            typ = "dpop+jwt",
            alg = "ES256",
            jwk = new { kty = "EC", crv = "P-256", x, y }
        });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jti,
            iat = issuedAt.ToUnixTimeSeconds(),
            htm = method.ToUpperInvariant(),
            htu = DpopProofValidator.CanonicalHtu(uri),
            ath = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            nonce,
            bh = Base64Url(SHA256.HashData(body))
        });
        var headerSegment = Base64Url(header);
        var payloadSegment = Base64Url(payload);
        var signingInput = Encoding.ASCII.GetBytes(headerSegment + "." + payloadSegment);
        var signature = key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return headerSegment + "." + payloadSegment + "." + Base64Url(signature);
    }

    private static string JwkThumbprint(ECDsa key)
    {
        var p = key.ExportParameters(false);
        var x = Base64Url(p.Q.X!);
        var y = Base64Url(p.Q.Y!);
        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class RecordingReplayStore : IDpopReplayStore
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public byte[]? LastDigest { get; private set; }
        public TimeSpan LastTtl { get; private set; }

        public ValueTask<bool> TryRegisterAsync(ReadOnlyMemory<byte> jtiSha256, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastDigest = jtiSha256.ToArray();
            LastTtl = ttl;
            return ValueTask.FromResult(_seen.Add(Convert.ToHexString(LastDigest)));
        }
    }

    private sealed class StaticNonceValidator(string nonce) : IAevrixServerNonceValidator
    {
        public string Nonce { get; } = nonce;
        public ValueTask<bool> IsCurrentNonceAsync(string jwkThumbprint, string candidate, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(string.Equals(candidate, Nonce, StringComparison.Ordinal));
        }
    }
}
