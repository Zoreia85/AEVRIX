using System.Net;
using System.Net.Security;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevrix.Core;

public enum AevrixTransportRequestPolicy
{
    /// <summary>Unauthenticated, non-sensitive challenge request used only to obtain the first server nonce.</summary>
    NonceChallenge,
    /// <summary>Device enrollment request. Requires short token, nonce and DPoP, but occurs before mTLS certificate issuance.</summary>
    Enrollment,
    /// <summary>Normal product request. Requires short token, nonce, DPoP and mTLS.</summary>
    Protected
}

public sealed record AevrixSessionSnapshot(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string ServerNonce);

public interface IAevrixSessionProvider
{
    ValueTask<AevrixSessionSnapshot?> GetSessionAsync(CancellationToken cancellationToken = default);
    void ObserveServerNonce(string nonce);
}

public interface IAevrixDeviceSigningKey : IDisposable
{
    string KeyId { get; }
    string SecurityTier { get; }
    AevrixPublicJwk PublicJwk { get; }
    byte[] ExportSubjectPublicKeyInfo();
    byte[] SignEs256(ReadOnlySpan<byte> data);
}

public sealed record AevrixPublicJwk(
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("crv")] string Crv,
    [property: JsonPropertyName("x")] string X,
    [property: JsonPropertyName("y")] string Y);

public sealed record AevrixSecureTransportOptions(
    Uri BaseUri,
    IReadOnlyList<string> SpkiSha256Pins,
    string NonceChallengePath = "/v1/device/challenge",
    int MaxRequestBodyBytes = 8 * 1024 * 1024,
    int MaxBufferedResponseBytes = 16 * 1024 * 1024,
    TimeSpan? Timeout = null)
{
    public AevrixSecureTransportOptions Validate()
    {
        ArgumentNullException.ThrowIfNull(BaseUri);
        if (!BaseUri.IsAbsoluteUri || BaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("AEVRIX remote transport requires an absolute HTTPS BaseUri.", nameof(BaseUri));
        }
        if (!string.IsNullOrEmpty(BaseUri.UserInfo) || !string.IsNullOrEmpty(BaseUri.Query) || !string.IsNullOrEmpty(BaseUri.Fragment))
        {
            throw new ArgumentException("AEVRIX BaseUri must not contain credentials, query or fragment.", nameof(BaseUri));
        }
        if (BaseUri.AbsolutePath != "/" && !BaseUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("AEVRIX BaseUri path prefixes must end with '/'.", nameof(BaseUri));
        }
        if (!NonceChallengePath.StartsWith("/", StringComparison.Ordinal) || NonceChallengePath.Contains("?", StringComparison.Ordinal) || NonceChallengePath.Contains("#", StringComparison.Ordinal))
        {
            throw new ArgumentException("Nonce challenge path must be an absolute path without query or fragment.", nameof(NonceChallengePath));
        }
        if (MaxRequestBodyBytes is < 0 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRequestBodyBytes));
        }
        if (MaxBufferedResponseBytes is < 1024 or > 256 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBufferedResponseBytes));
        }
        if (Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }

        _ = new SpkiPinSet(SpkiSha256Pins);
        return this;
    }
}

public sealed record AevrixTransportResponse(
    HttpStatusCode StatusCode,
    byte[] Body,
    string? MediaType,
    string? ServerNonce,
    IReadOnlyDictionary<string, string[]> Headers)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}

public interface IAevrixSecureTransport : IDisposable
{
    Task<AevrixTransportResponse> SendAsync(
        HttpMethod method,
        string relativeOrAbsoluteUri,
        AevrixTransportRequestPolicy policy,
        ReadOnlyMemory<byte> body = default,
        string? mediaType = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);
}

public sealed class AevrixSecureTransport : IAevrixSecureTransport
{
    public const string DpopHeaderName = "DPoP";
    public const string DpopNonceHeaderName = "DPoP-Nonce";
    public const string BodyHashHeaderName = "X-AEVRIX-Body-SHA256";
    public const string ProtocolHeaderName = "X-AEVRIX-Transport";
    public const string ProtocolVersion = "1";

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        DpopHeaderName,
        DpopNonceHeaderName,
        BodyHashHeaderName,
        ProtocolHeaderName,
        "Host",
        "Cookie"
    };

    private readonly AevrixSecureTransportOptions _options;
    private readonly IAevrixSessionProvider _sessions;
    private readonly IAevrixDeviceSigningKey _deviceKey;
    private readonly X509Certificate2? _clientCertificate;
    private readonly SpkiPinSet _pins;
    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public AevrixSecureTransport(
        AevrixSecureTransportOptions options,
        IAevrixSessionProvider sessions,
        IAevrixDeviceSigningKey deviceKey,
        X509Certificate2? clientCertificate = null,
        TimeProvider? timeProvider = null)
    {
        _options = options.Validate();
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _deviceKey = deviceKey ?? throw new ArgumentNullException(nameof(deviceKey));
        _clientCertificate = clientCertificate;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pins = new SpkiPinSet(_options.SpkiSha256Pins);

        if (_clientCertificate is not null)
        {
            ValidateClientCertificate(_clientCertificate);
        }

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            CheckCertificateRevocationList = true,
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = ValidateServerCertificate
        };
        if (_clientCertificate is not null)
        {
            handler.ClientCertificates.Add(_clientCertificate);
        }

        _client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = _options.BaseUri,
            Timeout = _options.Timeout ?? TimeSpan.FromSeconds(45)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AEVRIX/0.001");
    }

    public async Task<AevrixTransportResponse> SendAsync(
        HttpMethod method,
        string relativeOrAbsoluteUri,
        AevrixTransportRequestPolicy policy,
        ReadOnlyMemory<byte> body = default,
        string? mediaType = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeOrAbsoluteUri);
        if (body.Length > _options.MaxRequestBodyBytes)
        {
            throw new InvalidOperationException("AEVRIX request body exceeds the configured transport limit.");
        }

        var requestUri = ResolveBoundUri(relativeOrAbsoluteUri);
        EnforcePolicyEndpoint(policy, method, requestUri, body.Length);
        ValidateCustomHeaders(headers);

        var exactBody = body.ToArray();
        var bodyHash = Base64Url(SHA256.HashData(exactBody));
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation(ProtocolHeaderName, ProtocolVersion);
        request.Headers.TryAddWithoutValidation(BodyHashHeaderName, bodyHash);

        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                {
                    throw new InvalidOperationException($"Custom transport header is invalid: {pair.Key}");
                }
            }
        }

        if (exactBody.Length > 0 || mediaType is not null)
        {
            var content = new ByteArrayContent(exactBody);
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
            }
            request.Content = content;
        }

        if (policy is not AevrixTransportRequestPolicy.NonceChallenge)
        {
            var session = await RequireFreshSessionAsync(cancellationToken);
            if (policy is AevrixTransportRequestPolicy.Protected && _clientCertificate is null)
            {
                throw new InvalidOperationException("Protected AEVRIX transport requires an enrolled mTLS device certificate.");
            }
            var proof = CreateDpopProof(method, requestUri, exactBody, session);
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", session.AccessToken);
            request.Headers.TryAddWithoutValidation(DpopHeaderName, proof);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var observedNonce = ObserveNonce(response);
        var responseBody = await ReadBoundedBodyAsync(response, cancellationToken);
        var responseHeaders = SnapshotResponseHeaders(response);
        return new AevrixTransportResponse(
            response.StatusCode,
            responseBody,
            response.Content.Headers.ContentType?.MediaType,
            observedNonce,
            responseHeaders);
    }

    public string CreateDpopProof(
        HttpMethod method,
        Uri requestUri,
        ReadOnlySpan<byte> exactBody,
        AevrixSessionSnapshot session)
    {
        ValidateSession(session);
        var bound = ResolveBoundUri(requestUri.AbsoluteUri);
        var htu = CanonicalHtu(bound);
        var now = _timeProvider.GetUtcNow();
        if (session.ExpiresAt - now <= TimeSpan.FromSeconds(15))
        {
            throw new InvalidOperationException("AEVRIX remote session is expired or too close to expiry for DPoP proof creation.");
        }
        var accessTokenHash = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(session.AccessToken)));
        var bodyHash = Base64Url(SHA256.HashData(exactBody));
        var jti = Base64Url(RandomNumberGenerator.GetBytes(16));

        var header = new DpopHeader("dpop+jwt", "ES256", _deviceKey.PublicJwk);
        var payload = new DpopPayload(
            jti,
            now.ToUnixTimeSeconds(),
            method.Method.ToUpperInvariant(),
            htu,
            accessTokenHash,
            session.ServerNonce,
            bodyHash);
        var headerSegment = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadSegment = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = Encoding.ASCII.GetBytes(headerSegment + "." + payloadSegment);
        var signature = _deviceKey.SignEs256(signingInput);
        if (signature.Length != 64)
        {
            throw new CryptographicException("ES256 DPoP signature must use 64-byte IEEE P1363 encoding.");
        }
        return headerSegment + "." + payloadSegment + "." + Base64Url(signature);
    }

    public static string CanonicalHtu(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("DPoP htu requires an absolute HTTPS URI without userinfo.", nameof(uri));
        }
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Host = uri.IdnHost.ToLowerInvariant()
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private async ValueTask<AevrixSessionSnapshot> RequireFreshSessionAsync(CancellationToken cancellationToken)
    {
        var session = await _sessions.GetSessionAsync(cancellationToken)
            ?? throw new InvalidOperationException("AEVRIX remote session is unavailable.");
        ValidateSession(session);
        var remaining = session.ExpiresAt - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.FromSeconds(15))
        {
            throw new InvalidOperationException("AEVRIX remote session is expired or too close to expiry.");
        }
        return session;
    }

    private static void ValidateSession(AevrixSessionSnapshot session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AccessToken);
        if (!IsSafeNonce(session.ServerNonce))
        {
            throw new InvalidOperationException("AEVRIX server nonce is missing or malformed.");
        }
    }

    private string? ObserveNonce(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(DpopNonceHeaderName, out var values))
        {
            return null;
        }
        var nonce = values.FirstOrDefault();
        if (!IsSafeNonce(nonce))
        {
            return null;
        }
        _sessions.ObserveServerNonce(nonce!);
        return nonce;
    }

    private static bool IsSafeNonce(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length is < 16 or > 512)
        {
            return false;
        }
        return nonce.All(ch => ch is >= '!' and <= '~' && ch is not '"' and not '\\');
    }

    private async Task<byte[]> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared > _options.MaxBufferedResponseBytes)
        {
            throw new InvalidDataException("AEVRIX response exceeded the configured buffered-response limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(declared is > 0 and <= int.MaxValue ? (int)declared.Value : 0);
        var block = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > _options.MaxBufferedResponseBytes)
            {
                throw new InvalidDataException("AEVRIX response exceeded the configured buffered-response limit.");
            }
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private Uri ResolveBoundUri(string relativeOrAbsoluteUri)
    {
        if (relativeOrAbsoluteUri.Contains("#", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AEVRIX transport request URIs must not contain fragments.");
        }

        Uri resolved;
        if (Uri.TryCreate(relativeOrAbsoluteUri, UriKind.Absolute, out var absolute)
            && string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            resolved = absolute;
        }
        else
        {
            resolved = new Uri(_options.BaseUri, relativeOrAbsoluteUri);
        }

        if (resolved.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(resolved.UserInfo)
            || !SameAuthority(_options.BaseUri, resolved))
        {
            throw new InvalidOperationException("AEVRIX transport refused a request outside its exact HTTPS authority.");
        }

        var basePath = _options.BaseUri.AbsolutePath;
        if (!basePath.EndsWith("/", StringComparison.Ordinal))
        {
            basePath += "/";
        }
        if (_options.BaseUri.AbsolutePath != "/"
            && !resolved.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AEVRIX transport request escaped its configured API path prefix.");
        }
        return resolved;
    }

    private static bool SameAuthority(Uri expected, Uri actual) =>
        string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.OrdinalIgnoreCase)
        && EffectivePort(expected) == EffectivePort(actual);

    private static int EffectivePort(Uri uri) => uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : uri.Port) : uri.Port;

    private void EnforcePolicyEndpoint(AevrixTransportRequestPolicy policy, HttpMethod method, Uri uri, int bodyLength)
    {
        if (policy is not AevrixTransportRequestPolicy.NonceChallenge)
        {
            return;
        }
        if (method != HttpMethod.Get || bodyLength != 0 || !string.Equals(uri.AbsolutePath, _options.NonceChallengePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unauthenticated transport is permitted only for the exact nonce-challenge GET endpoint.");
        }
    }

    private static void ValidateCustomHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }
        foreach (var pair in headers)
        {
            if (ReservedHeaders.Contains(pair.Key)
                || pair.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || pair.Key.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase)
                || pair.Value.Contains('\r')
                || pair.Value.Contains('\n'))
            {
                throw new InvalidOperationException($"Custom transport header is reserved or unsafe: {pair.Key}");
            }
        }
    }

    private bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors != SslPolicyErrors.None || certificate is null)
        {
            return false;
        }
        if (request.RequestUri is null || !SameAuthority(_options.BaseUri, request.RequestUri))
        {
            return false;
        }
        return _pins.Matches(certificate);
    }

    private static void ValidateClientCertificate(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("AEVRIX mTLS certificate has no private key.");
        }
        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
        {
            throw new InvalidOperationException("AEVRIX mTLS certificate is not currently valid.");
        }
        using var ecdsa = certificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("AEVRIX mTLS certificate must use an ECDSA private key.");
        if (ecdsa.KeySize != 256)
        {
            throw new InvalidOperationException("AEVRIX mTLS certificate must use ECDSA P-256.");
        }
    }

    private static IReadOnlyDictionary<string, string[]> SnapshotResponseHeaders(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result[header.Key] = header.Value.Take(32).Select(value => value.Length <= 2048 ? value : value[..2048]).ToArray();
        }
        foreach (var header in response.Content.Headers)
        {
            result[header.Key] = header.Value.Take(32).Select(value => value.Length <= 2048 ? value : value[..2048]).ToArray();
        }
        return result;
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Device identity and client certificate are injected shared security state.
        // Their lifetime is owned by the caller, not by an individual transport instance.
        _client.Dispose();
    }

    private sealed record DpopHeader(
        [property: JsonPropertyName("typ")] string Typ,
        [property: JsonPropertyName("alg")] string Alg,
        [property: JsonPropertyName("jwk")] AevrixPublicJwk Jwk);

    private sealed record DpopPayload(
        [property: JsonPropertyName("jti")] string Jti,
        [property: JsonPropertyName("iat")] long Iat,
        [property: JsonPropertyName("htm")] string Htm,
        [property: JsonPropertyName("htu")] string Htu,
        [property: JsonPropertyName("ath")] string Ath,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("bh")] string BodyHash);
}

public sealed class SpkiPinSet
{
    private readonly byte[][] _pins;

    public SpkiPinSet(IReadOnlyList<string> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count < 2)
        {
            throw new InvalidOperationException("AEVRIX SPKI policy requires a current pin and at least one backup/rotation pin.");
        }

        _pins = pins.Select(ParsePin).ToArray();
        if (_pins.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count() < 2)
        {
            throw new InvalidOperationException("AEVRIX SPKI current and backup pins must be distinct.");
        }
    }

    public bool Matches(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var spki = ExportSubjectPublicKeyInfo(certificate);
        var hash = SHA256.HashData(spki);
        return _pins.Any(pin => CryptographicOperations.FixedTimeEquals(pin, hash));
    }

    public static string ComputePin(X509Certificate2 certificate)
    {
        var hash = SHA256.HashData(ExportSubjectPublicKeyInfo(certificate));
        return "sha256/" + Convert.ToBase64String(hash);
    }

    private static byte[] ParsePin(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!value.StartsWith("sha256/", StringComparison.Ordinal))
        {
            throw new FormatException("AEVRIX SPKI pins must use sha256/<base64> format.");
        }
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value[7..]);
        }
        catch (FormatException exception)
        {
            throw new FormatException("AEVRIX SPKI pin is not valid base64.", exception);
        }
        if (decoded.Length != 32)
        {
            throw new FormatException("AEVRIX SPKI SHA-256 pins must decode to 32 bytes.");
        }
        return decoded;
    }

    private static byte[] ExportSubjectPublicKeyInfo(X509Certificate2 certificate)
    {
        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is not null)
        {
            return ecdsa.ExportSubjectPublicKeyInfo();
        }
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            return rsa.ExportSubjectPublicKeyInfo();
        }
        throw new CryptographicException("AEVRIX cannot pin an unsupported server public-key algorithm.");
    }
}
