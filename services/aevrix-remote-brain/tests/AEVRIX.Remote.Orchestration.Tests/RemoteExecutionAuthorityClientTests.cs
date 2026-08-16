using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class RemoteExecutionAuthorityClientTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Secret = Enumerable.Repeat((byte)0x5a, 32).ToArray();

    [TestMethod]
    public async Task LoadAsync_UsesHmacBoundRequestAndParsesHead()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CapturedRequest? captured = null;
        var handler = new DelegateHandler(async request =>
        {
            captured = await CapturedRequest.FromAsync(request);
            return Json(HttpStatusCode.OK, new { entryCount = 5, headHashSha256 = H('3') });
        });
        using var http = new InMemoryAuthorityHttpClient(handler);
        var client = Client(http, signingKey);

        var head = await client.LoadAsync(Project);

        Assert.IsNotNull(head);
        Assert.AreEqual(5, head.EntryCount);
        Assert.AreEqual(H('3'), head.HeadHashSha256);
        Assert.IsNotNull(captured);
        Assert.AreEqual("client-test", captured.Headers["X-AEVRIX-Client-Id"]);
        Assert.AreEqual(Now.ToUnixTimeSeconds().ToString(), captured.Headers["X-AEVRIX-Timestamp"]);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(), captured.Headers["X-AEVRIX-Body-SHA256"]);
        Assert.AreEqual(32, captured.Headers["X-AEVRIX-Nonce"].Length);

        var canonical = string.Join("\n", new[]
        {
            "AEVRIX-AUTHORITY-REQUEST-V1",
            "GET",
            $"/v1/projects/{Project:D}/head",
            Now.ToUnixTimeSeconds().ToString(),
            captured.Headers["X-AEVRIX-Nonce"],
            captured.Headers["X-AEVRIX-Body-SHA256"]
        });
        var expected = Convert.ToBase64String(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(canonical)));
        Assert.AreEqual(expected, captured.Headers["X-AEVRIX-Request-Signature"]);
    }

    [TestMethod]
    public async Task AdvanceAsync_ConflictFailsClosed()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var http = new InMemoryAuthorityHttpClient(new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict))));
        var client = Client(http, signingKey);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AdvanceAsync(Project, new ExecutionProofHead(4, H('2')), new ExecutionProofHead(5, H('3'))));
    }

    [TestMethod]
    public async Task PromotionAttestation_VerifiesPinnedSignatureAndKnownEvidenceVector()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        Assert.AreEqual("2064dc617a9710d1ff7f96c14628b58a28740e15cbcc3b16e680ee95d7acea8b", evidence.ComputeDigestSha256());

        using var http = new InMemoryAuthorityHttpClient(new DelegateHandler(_ =>
        {
            var attestation = SignedAttestation(signingKey, evidence, tamperSignature: false);
            return Task.FromResult(Json(HttpStatusCode.OK, attestation));
        }));
        var client = Client(http, signingKey);

        var result = await client.RequestPromotionAttestationAsync(evidence);

        Assert.AreEqual("authority-key-01", result.KeyId);
        Assert.AreEqual(Project, result.ProjectId);
        Assert.AreEqual(evidence.ComputeDigestSha256(), result.EvidenceDigestSha256);
        Assert.AreEqual(evidence.LedgerHead.HeadHashSha256, result.HeadHashSha256);
    }

    [TestMethod]
    public async Task PromotionAttestation_RejectsForgedSignature()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        using var http = new InMemoryAuthorityHttpClient(new DelegateHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.OK, SignedAttestation(signingKey, evidence, tamperSignature: true)))));
        var client = Client(http, signingKey);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.RequestPromotionAttestationAsync(evidence));
    }

    [TestMethod]
    public async Task PromotionAttestation_RejectsAuthorityHeadMismatchBeforeTrustingSignature()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var evidence = Evidence();
        using var http = new InMemoryAuthorityHttpClient(new DelegateHandler(_ =>
        {
            var signed = SignedAttestation(signingKey, evidence, tamperSignature: false) with
            {
                HeadHashSha256 = H('4')
            };
            return Task.FromResult(Json(HttpStatusCode.OK, signed));
        }));
        var client = Client(http, signingKey);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.RequestPromotionAttestationAsync(evidence));
    }

    [TestMethod]
    public void Options_RejectRemotePlainHttp()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new RemoteExecutionAuthorityClientOptions(
            new Uri("http://authority.example.test/"),
            TimeSpan.FromSeconds(5),
            "authority-key-01",
            signingKey.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [TestMethod]
    public async Task RequestPayload_DoesNotContainAuthenticationSecret()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CapturedRequest? captured = null;
        using var http = new InMemoryAuthorityHttpClient(new DelegateHandler(async request =>
        {
            captured = await CapturedRequest.FromAsync(request);
            return Json(HttpStatusCode.OK, new { entryCount = 5, headHashSha256 = H('3') });
        }));
        var client = Client(http, signingKey);
        await client.AdvanceAsync(Project, new ExecutionProofHead(4, H('2')), new ExecutionProofHead(5, H('3')));

        Assert.IsNotNull(captured);
        Assert.IsFalse(captured.Body.AsSpan().IndexOf(Secret) >= 0);
        Assert.IsFalse(Encoding.UTF8.GetString(captured.Body).Contains("client-test", StringComparison.Ordinal));
    }

    private static RemoteExecutionAuthorityClient Client(HttpClient http, ECDsa signingKey) =>
        new(
            http,
            new FixedCredentialProvider(),
            new RemoteExecutionAuthorityClientOptions(
                new Uri("http://127.0.0.1:18081/"),
                TimeSpan.FromSeconds(5),
                "authority-key-01",
                signingKey.ExportSubjectPublicKeyInfoPem(),
                AllowLoopbackHttp: true),
            new FixedTimeProvider(Now));

    private static PromotionEvidenceEnvelope Evidence() => new(
        1,
        Project,
        "run-vector",
        "exec-vector",
        "coding-agent",
        "sandbox-worker",
        H('e'),
        H('f'),
        H('1'),
        H('2'),
        H('3'),
        new ExecutionProofHead(5, H('3')));

    private static PromotionAuthorityAttestation SignedAttestation(
        ECDsa key,
        PromotionEvidenceEnvelope evidence,
        bool tamperSignature)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var unsigned = new PromotionAuthorityAttestation(
            1,
            "authority-key-01",
            evidence.ProjectId,
            evidence.RunId,
            evidence.ExecutionId,
            evidence.ComputeDigestSha256(),
            evidence.LedgerHead.EntryCount,
            evidence.LedgerHead.HeadHashSha256,
            Now.ToUnixTimeSeconds() - 5,
            Now.AddMinutes(5).ToUnixTimeSeconds(),
            "0123456789abcdef0123456789abcdef",
            "AA==",
            fingerprint);
        var signature = key.SignData(
            unsigned.CanonicalPayloadUtf8(),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        if (tamperSignature) signature[^1] ^= 0x01;
        return unsigned with { SignatureDerBase64 = Convert.ToBase64String(signature) };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };

    private static string H(char value) => new(value, 64);

    private sealed class FixedCredentialProvider : IExecutionAuthorityCredentialProvider
    {
        public ValueTask<ExecutionAuthorityCredential> GetCredentialAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ExecutionAuthorityCredential("client-test", Secret));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemoryAuthorityHttpClient(HttpMessageHandler handler) : HttpClient(handler);

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }

    private sealed record CapturedRequest(Dictionary<string, string> Headers, byte[] Body)
    {
        public static async Task<CapturedRequest> FromAsync(HttpRequestMessage request)
        {
            var headers = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => string.Join(",", pair.Value),
                StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync();
            return new CapturedRequest(headers, body);
        }
    }
}
