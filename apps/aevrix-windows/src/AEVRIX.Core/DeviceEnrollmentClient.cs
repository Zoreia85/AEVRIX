using System.Runtime.Versioning;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aevrix.Core;

public sealed record AevrixDeviceEnrollmentEndpoints(
    string ChallengePath = "/v1/device/challenge",
    string EnrollmentPath = "/v1/device/enroll")
{
    public AevrixDeviceEnrollmentEndpoints Validate()
    {
        ValidatePath(ChallengePath, nameof(ChallengePath));
        ValidatePath(EnrollmentPath, nameof(EnrollmentPath));
        if (string.Equals(ChallengePath, EnrollmentPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("Device challenge and enrollment endpoints must be distinct.");
        }
        return this;
    }

    private static void ValidatePath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains("?", StringComparison.Ordinal)
            || value.Contains("#", StringComparison.Ordinal)
            || value.Contains('\\'))
        {
            throw new ArgumentException("AEVRIX remote endpoint must be a canonical absolute path without authority, query or fragment.", parameterName);
        }
    }
}

public sealed record AevrixDeviceEnrollmentReceipt(
    string DeviceId,
    string CertificateSha256,
    string CertificateThumbprint,
    DateTimeOffset CertificateNotAfter,
    string? PolicyVersion);

public sealed class AevrixDeviceEnrollmentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    private readonly IAevrixSecureTransport _transport;
    private readonly AevrixDeviceEnrollmentEndpoints _endpoints;

    public AevrixDeviceEnrollmentClient(
        IAevrixSecureTransport transport,
        AevrixDeviceEnrollmentEndpoints? endpoints = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _endpoints = (endpoints ?? new AevrixDeviceEnrollmentEndpoints()).Validate();
    }

    public async Task<string> PrimeServerNonceAsync(CancellationToken cancellationToken = default)
    {
        var response = await _transport.SendAsync(
            HttpMethod.Get,
            _endpoints.ChallengePath,
            AevrixTransportRequestPolicy.NonceChallenge,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw EnrollmentFailure("device_challenge_rejected", response.StatusCode, response.Body);
        }
        if (string.IsNullOrWhiteSpace(response.ServerNonce))
        {
            throw new InvalidDataException("AEVRIX device challenge succeeded without a usable server nonce.");
        }
        return response.ServerNonce;
    }

    [SupportedOSPlatform("windows")]
    public async Task<AevrixDeviceEnrollmentReceipt> EnrollAsync(
        DeviceEnrollmentMaterial material,
        WindowsDeviceSigningKey localKey,
        string clientVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(localKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        if (!string.Equals(material.InstallationId, localKey.InstallationId, StringComparison.Ordinal)
            || !string.Equals(material.KeyId, localKey.KeyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Enrollment material is not bound to the supplied local device key.");
        }

        var request = new DeviceEnrollmentRequest(
            material.InstallationId,
            material.KeyId,
            localKey.SecurityTier,
            material.PublicJwk,
            material.CsrPem,
            clientVersion.Trim());
        var body = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var response = await _transport.SendAsync(
            HttpMethod.Post,
            _endpoints.EnrollmentPath,
            AevrixTransportRequestPolicy.Enrollment,
            body,
            "application/json",
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw EnrollmentFailure("device_enrollment_rejected", response.StatusCode, response.Body);
        }

        var payload = JsonSerializer.Deserialize<DeviceEnrollmentResponse>(response.Body, JsonOptions)
            ?? throw new InvalidDataException("AEVRIX device enrollment response was empty or invalid.");
        if (string.IsNullOrWhiteSpace(payload.DeviceId)
            || string.IsNullOrWhiteSpace(payload.CertificateDerBase64)
            || string.IsNullOrWhiteSpace(payload.CertificateSha256))
        {
            throw new InvalidDataException("AEVRIX device enrollment response omitted required certificate fields.");
        }

        byte[] certificateDer;
        try
        {
            certificateDer = Convert.FromBase64String(payload.CertificateDerBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("AEVRIX device enrollment certificate encoding is invalid.", exception);
        }
        if (certificateDer.Length is < 256 or > 64 * 1024)
        {
            throw new InvalidDataException("AEVRIX device enrollment certificate size is outside the allowed range.");
        }

        var expectedHash = NormalizeSha256(payload.CertificateSha256);
        var actualHash = Convert.ToHexString(SHA256.HashData(certificateDer)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash),
                Convert.FromHexString(actualHash)))
        {
            throw new InvalidDataException("AEVRIX issued certificate SHA-256 does not match the enrollment response.");
        }

        var installed = localKey.InstallIssuedClientCertificate(certificateDer);
        if (!string.Equals(installed.Sha256Fingerprint, actualHash, StringComparison.Ordinal))
        {
            throw new CryptographicException("AEVRIX installed device certificate fingerprint changed unexpectedly.");
        }
        return new AevrixDeviceEnrollmentReceipt(
            payload.DeviceId.Trim(),
            actualHash,
            installed.Thumbprint,
            installed.NotAfter,
            string.IsNullOrWhiteSpace(payload.PolicyVersion) ? null : payload.PolicyVersion.Trim());
    }

    private static Exception EnrollmentFailure(string fallbackCode, HttpStatusCode statusCode, byte[] body)
    {
        var code = fallbackCode;
        if (body.Length is > 0 and <= 16 * 1024)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("code", out var codeElement)
                    && codeElement.ValueKind == JsonValueKind.String
                    && IsSafeCode(codeElement.GetString()))
                {
                    code = codeElement.GetString()!;
                }
            }
            catch (JsonException)
            {
                // Failure responses are intentionally not surfaced verbatim; only a bounded safe code is retained.
            }
        }
        return new InvalidOperationException($"AEVRIX enrollment request failed ({(int)statusCode}, {code}).");
    }

    private static bool IsSafeCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 80
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException("AEVRIX enrollment certificate SHA-256 is invalid.");
        }
        return normalized;
    }

    private sealed record DeviceEnrollmentRequest(
        string InstallationId,
        string KeyId,
        string SecurityTier,
        AevrixPublicJwk PublicJwk,
        string CsrPem,
        string ClientVersion);

    private sealed record DeviceEnrollmentResponse(
        string DeviceId,
        string CertificateDerBase64,
        string CertificateSha256,
        string? PolicyVersion);
}
