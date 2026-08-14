using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class SecureTransportTests
{
    [TestMethod]
    public void OptionsRequireHttpsExactAuthorityAndTwoDistinctSpkiPins()
    {
        using var first = CreateCertificate("CN=pin-a");
        using var second = CreateCertificate("CN=pin-b");
        var pins = new[] { SpkiPinSet.ComputePin(first), SpkiPinSet.ComputePin(second) };

        var valid = new AevrixSecureTransportOptions(new Uri("https://api.aevrix.example/"), pins).Validate();
        Assert.AreEqual("api.aevrix.example", valid.BaseUri.Host);

        Assert.ThrowsExactly<ArgumentException>(() =>
            new AevrixSecureTransportOptions(new Uri("http://api.aevrix.example/"), pins).Validate());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AevrixSecureTransportOptions(new Uri("https://user:pass@api.aevrix.example/"), pins).Validate());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new AevrixSecureTransportOptions(new Uri("https://api.aevrix.example/"), [pins[0]]).Validate());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new AevrixSecureTransportOptions(new Uri("https://api.aevrix.example/"), [pins[0], pins[0]]).Validate());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AevrixSecureTransportOptions(new Uri("https://api.aevrix.example/v1"), pins).Validate());
    }

    [TestMethod]
    public void SpkiPinBindsPublicKeyNotWholeCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var requestA = new CertificateRequest("CN=a", key, HashAlgorithmName.SHA256);
        var requestB = new CertificateRequest("CN=b", key, HashAlgorithmName.SHA256);
        using var certA = requestA.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var certB = requestB.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(3));
        using var backup = CreateCertificate("CN=backup");

        var pinA = SpkiPinSet.ComputePin(certA);
        var pinB = SpkiPinSet.ComputePin(certB);
        Assert.AreEqual(pinA, pinB, "Certificate renewal with the same public key must preserve the SPKI pin.");
        var set = new SpkiPinSet([pinA, SpkiPinSet.ComputePin(backup)]);
        Assert.IsTrue(set.Matches(certB));
    }

    [TestMethod]
    public void DpopProofUsesEs256UniqueJtiAndBindsTokenMethodUriNonceAndBody()
    {
        using var signingKey = new EphemeralSigningKey();
        using var pinA = CreateCertificate("CN=pin-a");
        using var pinB = CreateCertificate("CN=pin-b");
        var session = new AevrixSessionSnapshot(
            "short-lived-access-token",
            DateTimeOffset.Parse("2026-08-14T04:05:00Z"),
            "server-nonce-1234567890");
        var provider = new StaticSessionProvider(session);
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T04:00:00Z"));
        using var transport = new AevrixSecureTransport(
            new AevrixSecureTransportOptions(
                new Uri("https://api.aevrix.example/"),
                [SpkiPinSet.ComputePin(pinA), SpkiPinSet.ComputePin(pinB)]),
            provider,
            signingKey,
            timeProvider: time);

        var uri = new Uri("https://api.aevrix.example/v1/blueprint?project=secret#not-sent");
        var body = Encoding.UTF8.GetBytes("{\"captureId\":\"capture-1\"}");
        var first = transport.CreateDpopProof(HttpMethod.Post, uri, body, session);
        var second = transport.CreateDpopProof(HttpMethod.Post, uri, body, session);
        Assert.AreNotEqual(first, second, "Every DPoP proof must have a unique jti/signature.");

        var parts = first.Split('.');
        Assert.AreEqual(3, parts.Length);
        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        Assert.AreEqual("dpop+jwt", header.RootElement.GetProperty("typ").GetString());
        Assert.AreEqual("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.AreEqual("EC", header.RootElement.GetProperty("jwk").GetProperty("kty").GetString());
        Assert.AreEqual("P-256", header.RootElement.GetProperty("jwk").GetProperty("crv").GetString());

        Assert.AreEqual("POST", payload.RootElement.GetProperty("htm").GetString());
        Assert.AreEqual("https://api.aevrix.example/v1/blueprint", payload.RootElement.GetProperty("htu").GetString());
        Assert.AreEqual("server-nonce-1234567890", payload.RootElement.GetProperty("nonce").GetString());
        Assert.AreEqual(1786680000L, payload.RootElement.GetProperty("iat").GetInt64());
        Assert.AreEqual(Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(session.AccessToken))), payload.RootElement.GetProperty("ath").GetString());
        Assert.AreEqual(Base64Url(SHA256.HashData(body)), payload.RootElement.GetProperty("bh").GetString());
        Assert.IsTrue(payload.RootElement.GetProperty("jti").GetString()!.Length >= 20);

        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signature = Base64UrlDecode(parts[2]);
        Assert.AreEqual(64, signature.Length);
        using var verifier = ECDsa.Create(signingKey.PublicParameters);
        Assert.IsTrue(verifier.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [TestMethod]
    public void CanonicalHtuRemovesQueryAndFragmentButRetainsNonDefaultPort()
    {
        Assert.AreEqual(
            "https://api.aevrix.example:8443/v1/resource",
            AevrixSecureTransport.CanonicalHtu(new Uri("https://api.aevrix.example:8443/v1/resource?x=1#fragment")));
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    private sealed class StaticSessionProvider(AevrixSessionSnapshot session) : IAevrixSessionProvider
    {
        public string? ObservedNonce { get; private set; }
        public ValueTask<AevrixSessionSnapshot?> GetSessionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<AevrixSessionSnapshot?>(session);
        public void ObserveServerNonce(string nonce) => ObservedNonce = nonce;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class EphemeralSigningKey : IAevrixDeviceSigningKey
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECParameters PublicParameters => _key.ExportParameters(false);
        public string KeyId => "test-key";
        public string SecurityTier => "test-ephemeral";
        public AevrixPublicJwk PublicJwk
        {
            get
            {
                var p = PublicParameters;
                return new AevrixPublicJwk("EC", "P-256", Base64Url(p.Q.X!), Base64Url(p.Q.Y!));
            }
        }
        public byte[] ExportSubjectPublicKeyInfo() => _key.ExportSubjectPublicKeyInfo();
        public byte[] SignEs256(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        public void Dispose() => _key.Dispose();
    }
}
