using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class LiveHttpsEvidenceTests
{
    [TestMethod]
    [Timeout(90_000)]
    public async Task ExampleDomain_FlowsThroughRealTlsAndPinnedAevrixTransport()
    {
        var target = new Uri("https://example.com/");
        using var tcp = new TcpClient();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await tcp.ConnectAsync(target.IdnHost, target.Port, connectCts.Token);
        using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(target.IdnHost);

        var remote = ssl.RemoteCertificate
            ?? throw new InvalidOperationException("example.com did not present a TLS certificate.");
        using var observed = X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
        using var backup = CreateCertificate("CN=live-backup-pin");
        using var signingKey = new LiveSigningKey();
        var provider = new EmptySessionProvider();
        using var transport = new AevrixSecureTransport(
            new AevrixSecureTransportOptions(
                target,
                [SpkiPinSet.ComputePin(observed), SpkiPinSet.ComputePin(backup)],
                NonceChallengePath: "/",
                MaxBufferedResponseBytes: 256 * 1024,
                Timeout: TimeSpan.FromSeconds(30)),
            provider,
            signingKey);

        using var requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var response = await transport.SendAsync(
            HttpMethod.Get,
            "/",
            AevrixTransportRequestPolicy.NonceChallenge,
            cancellationToken: requestCts.Token);

        Assert.IsTrue(response.IsSuccessStatusCode);
        Assert.IsTrue(response.Body.Length > 100);
        StringAssert.Contains(Encoding.UTF8.GetString(response.Body), "Example Domain");
        Assert.AreEqual(64, Convert.ToHexString(SHA256.HashData(response.Body)).Length);
    }

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class EmptySessionProvider : IAevrixSessionProvider
    {
        public ValueTask<AevrixSessionSnapshot?> GetSessionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AevrixSessionSnapshot?>(null);

        public void ObserveServerNonce(string nonce)
        {
        }
    }

    private sealed class LiveSigningKey : IAevrixDeviceSigningKey
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string KeyId => "live-test-key";
        public string SecurityTier => "test-ephemeral";
        public AevrixPublicJwk PublicJwk
        {
            get
            {
                var p = _key.ExportParameters(false);
                return new AevrixPublicJwk("EC", "P-256", Base64Url(p.Q.X!), Base64Url(p.Q.Y!));
            }
        }

        public byte[] ExportSubjectPublicKeyInfo() => _key.ExportSubjectPublicKeyInfo();

        public byte[] SignEs256(ReadOnlySpan<byte> data) =>
            _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose() => _key.Dispose();
    }
}
