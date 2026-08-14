using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Remote.Security;

public sealed record DpopValidationOptions(
    TimeSpan MaxProofAge,
    TimeSpan AllowedClockSkew,
    TimeSpan ReplayTtl,
    int MaxProofBytes = 24 * 1024)
{
    public static DpopValidationOptions SecureDefault { get; } = new(
        MaxProofAge: TimeSpan.FromSeconds(90),
        AllowedClockSkew: TimeSpan.FromSeconds(5),
        ReplayTtl: TimeSpan.FromSeconds(120));

    public DpopValidationOptions Validate()
    {
        if (MaxProofAge <= TimeSpan.Zero || MaxProofAge > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxProofAge));
        }
        if (AllowedClockSkew < TimeSpan.Zero || AllowedClockSkew > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(AllowedClockSkew));
        }
        if (ReplayTtl < MaxProofAge + AllowedClockSkew || ReplayTtl > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(ReplayTtl));
        }
        if (MaxProofBytes is < 1024 or > 128 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxProofBytes));
        }
        return this;
    }
}

public sealed record DpopValidationInput(
    string Proof,
    string HttpMethod,
    Uri RequestUri,
    string AccessToken,
    ReadOnlyMemory<byte> ExactBody,
    string ExpectedJwkThumbprint,
    string ExpectedServerNonce,
    DateTimeOffset Now);

public sealed record DpopValidationResult(
    bool Valid,
    string Code,
    string? JwkThumbprint = null,
    string? JtiSha256 = null,
    DateTimeOffset? IssuedAt = null)
{
    public static DpopValidationResult Reject(string code) => new(false, code);
}

/// <summary>
/// Production implementations must atomically register the 32-byte SHA-256 digest and return false on reuse.
/// Raw attacker-controlled jti values are deliberately never passed to this store.
/// </summary>
public interface IDpopReplayStore
{
    ValueTask<bool> TryRegisterAsync(ReadOnlyMemory<byte> jtiSha256, TimeSpan ttl, CancellationToken cancellationToken = default);
}

public interface IAevrixServerNonceValidator
{
    ValueTask<bool> IsCurrentNonceAsync(
        string jwkThumbprint,
        string nonce,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed class DpopProofValidator
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    private readonly IDpopReplayStore _replayStore;
    private readonly IAevrixServerNonceValidator _nonceValidator;
    private readonly DpopValidationOptions _options;

    public DpopProofValidator(
        IDpopReplayStore replayStore,
        IAevrixServerNonceValidator nonceValidator,
        DpopValidationOptions? options = null)
    {
        _replayStore = replayStore ?? throw new ArgumentNullException(nameof(replayStore));
        _nonceValidator = nonceValidator ?? throw new ArgumentNullException(nameof(nonceValidator));
        _options = (options ?? DpopValidationOptions.SecureDefault).Validate();
    }

    public async Task<DpopValidationResult> ValidateAsync(
        DpopValidationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Proof)
            || Encoding.UTF8.GetByteCount(input.Proof) > _options.MaxProofBytes)
        {
            return DpopValidationResult.Reject("dpop_invalid_size");
        }
        if (!TryValidateBoundRequest(input, out var expectedHtu, out var expectedAth, out var expectedBh))
        {
            return DpopValidationResult.Reject("dpop_invalid_request_binding");
        }

        var segments = input.Proof.Split('.');
        if (segments.Length != 3 || segments.Any(segment => string.IsNullOrEmpty(segment) || segment.Length > _options.MaxProofBytes))
        {
            return DpopValidationResult.Reject("dpop_invalid_compact_jws");
        }

        byte[] headerBytes;
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            headerBytes = DecodeBase64Url(segments[0], 8 * 1024);
            payloadBytes = DecodeBase64Url(segments[1], 12 * 1024);
            signature = DecodeBase64Url(segments[2], 256);
        }
        catch (FormatException)
        {
            return DpopValidationResult.Reject("dpop_invalid_base64url");
        }
        if (signature.Length != 64)
        {
            return DpopValidationResult.Reject("dpop_invalid_signature_size");
        }

        try
        {
            using var headerDocument = JsonDocument.Parse(headerBytes, JsonOptions);
            using var payloadDocument = JsonDocument.Parse(payloadBytes, JsonOptions);
            var header = headerDocument.RootElement;
            var payload = payloadDocument.RootElement;
            if (header.ValueKind != JsonValueKind.Object || payload.ValueKind != JsonValueKind.Object
                || HasDuplicateProperties(header) || HasDuplicateProperties(payload))
            {
                return DpopValidationResult.Reject("dpop_invalid_json_root");
            }
            if (!IsExactString(header, "typ", "dpop+jwt") || !IsExactString(header, "alg", "ES256"))
            {
                return DpopValidationResult.Reject("dpop_invalid_header");
            }
            if (header.TryGetProperty("crit", out _)
                || !header.TryGetProperty("jwk", out var jwk)
                || jwk.ValueKind != JsonValueKind.Object
                || HasDuplicateProperties(jwk)
                || jwk.TryGetProperty("d", out _))
            {
                return DpopValidationResult.Reject("dpop_invalid_jwk");
            }

            if (!TryCreateVerifier(jwk, out var verifier, out var thumbprint))
            {
                return DpopValidationResult.Reject("dpop_invalid_jwk");
            }
            using (verifier)
            {
                if (!FixedTimeTextEquals(thumbprint, input.ExpectedJwkThumbprint))
                {
                    return DpopValidationResult.Reject("dpop_key_binding_mismatch");
                }
                var signingInput = Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]);
                if (!verifier.VerifyData(
                        signingInput,
                        signature,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return DpopValidationResult.Reject("dpop_invalid_signature");
                }
            }

            if (!TryGetSafeString(payload, "jti", 16, 200, out var jti)
                || !TryGetUnixTime(payload, "iat", out var issuedAt)
                || !TryGetSafeString(payload, "htm", 3, 16, out var method)
                || !TryGetSafeString(payload, "htu", 8, 4096, out var htu)
                || !TryGetSafeString(payload, "ath", 20, 128, out var ath)
                || !TryGetSafeString(payload, "nonce", 16, 512, out var nonce)
                || !TryGetSafeString(payload, "bh", 20, 128, out var bh))
            {
                return DpopValidationResult.Reject("dpop_missing_or_invalid_claim");
            }

            if (!string.Equals(method, input.HttpMethod.Trim().ToUpperInvariant(), StringComparison.Ordinal)
                || !string.Equals(htu, expectedHtu, StringComparison.Ordinal)
                || !FixedTimeTextEquals(ath, expectedAth)
                || !FixedTimeTextEquals(bh, expectedBh))
            {
                return DpopValidationResult.Reject("dpop_claim_binding_mismatch");
            }

            var age = input.Now - issuedAt;
            if (age < -_options.AllowedClockSkew || age > _options.MaxProofAge)
            {
                return DpopValidationResult.Reject("dpop_proof_expired_or_future");
            }
            if (!FixedTimeTextEquals(nonce, input.ExpectedServerNonce)
                || !await _nonceValidator.IsCurrentNonceAsync(thumbprint, nonce, input.Now, cancellationToken))
            {
                return DpopValidationResult.Reject("dpop_nonce_mismatch");
            }

            var jtiHash = SHA256.HashData(Encoding.UTF8.GetBytes(jti));
            if (!await _replayStore.TryRegisterAsync(jtiHash, _options.ReplayTtl, cancellationToken))
            {
                return DpopValidationResult.Reject("dpop_replay_detected");
            }

            return new DpopValidationResult(
                true,
                "ok",
                thumbprint,
                Convert.ToHexString(jtiHash).ToLowerInvariant(),
                issuedAt);
        }
        catch (JsonException)
        {
            return DpopValidationResult.Reject("dpop_invalid_json");
        }
        catch (CryptographicException)
        {
            return DpopValidationResult.Reject("dpop_invalid_crypto");
        }
    }

    public static string CanonicalHtu(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("DPoP request URI must be an absolute HTTPS URI without userinfo.", nameof(uri));
        }
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Host = uri.IdnHost.ToLowerInvariant()
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private static bool TryValidateBoundRequest(
        DpopValidationInput input,
        out string expectedHtu,
        out string expectedAth,
        out string expectedBh)
    {
        expectedHtu = string.Empty;
        expectedAth = string.Empty;
        expectedBh = string.Empty;
        if (string.IsNullOrWhiteSpace(input.HttpMethod)
            || input.HttpMethod.Length > 16
            || input.RequestUri is null
            || string.IsNullOrWhiteSpace(input.AccessToken)
            || input.AccessToken.Length > 64 * 1024
            || !IsSafeNonce(input.ExpectedServerNonce))
        {
            return false;
        }
        try
        {
            expectedHtu = CanonicalHtu(input.RequestUri);
        }
        catch (ArgumentException)
        {
            return false;
        }
        expectedAth = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(input.AccessToken)));
        expectedBh = Base64Url(SHA256.HashData(input.ExactBody.Span));
        return true;
    }

    private static bool TryCreateVerifier(JsonElement jwk, out ECDsa verifier, out string thumbprint)
    {
        verifier = null!;
        thumbprint = string.Empty;
        if (!IsExactString(jwk, "kty", "EC")
            || !IsExactString(jwk, "crv", "P-256")
            || !TryGetSafeString(jwk, "x", 40, 80, out var xText)
            || !TryGetSafeString(jwk, "y", 40, 80, out var yText))
        {
            return false;
        }
        byte[] x;
        byte[] y;
        try
        {
            x = DecodeBase64Url(xText, 64);
            y = DecodeBase64Url(yText, 64);
        }
        catch (FormatException)
        {
            return false;
        }
        if (x.Length != 32 || y.Length != 32)
        {
            return false;
        }
        try
        {
            verifier = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y }
            });
            var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{xText}\",\"y\":\"{yText}\"}}";
            thumbprint = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            return true;
        }
        catch (CryptographicException)
        {
            verifier?.Dispose();
            verifier = null!;
            return false;
        }
    }


    private static bool HasDuplicateProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryGetUnixTime(JsonElement root, string property, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var seconds))
        {
            return false;
        }
        try
        {
            value = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsExactString(JsonElement root, string property, string expected) =>
        root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool TryGetSafeString(
        JsonElement root,
        string property,
        int minLength,
        int maxLength,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        var text = element.GetString();
        if (string.IsNullOrEmpty(text) || text.Length < minLength || text.Length > maxLength || text.Any(char.IsControl))
        {
            return false;
        }
        value = text;
        return true;
    }

    private static bool IsSafeNonce(string value) =>
        value.Length is >= 16 and <= 512
        && value.All(ch => ch is >= '!' and <= '~' && ch is not '"' and not '\\');

    private static byte[] DecodeBase64Url(string value, int maxDecodedBytes)
    {
        if (string.IsNullOrEmpty(value)
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
        {
            throw new FormatException("Invalid base64url characters.");
        }
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        var decoded = Convert.FromBase64String(normalized);
        if (decoded.Length > maxDecodedBytes)
        {
            throw new FormatException("Decoded value exceeded its limit.");
        }
        return decoded;
    }

    private static bool FixedTimeTextEquals(string left, string right)
    {
        if (left.Length > 4096 || right.Length > 4096)
        {
            return false;
        }
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
