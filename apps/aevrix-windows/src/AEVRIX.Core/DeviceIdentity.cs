using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Aevrix.Core;

public enum DeviceKeySecurityTier
{
    TpmNonExportable,
    SoftwareNonExportable
}

public sealed record DeviceEnrollmentMaterial(
    string InstallationId,
    string KeyId,
    DeviceKeySecurityTier SecurityTier,
    AevrixPublicJwk PublicJwk,
    string CsrPem);

public sealed record InstalledDeviceCertificate(
    string Thumbprint,
    string Sha256Fingerprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);

[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceSigningKey : IAevrixDeviceSigningKey
{
    private readonly ECDsaCng _ecdsa;
    private bool _disposed;

    internal WindowsDeviceSigningKey(string installationId, CngKey key, DeviceKeySecurityTier tier)
    {
        InstallationId = ValidateInstallationId(installationId);
        if (key.ExportPolicy != CngExportPolicies.None)
        {
            key.Dispose();
            throw new CryptographicException("AEVRIX device private key is exportable and was rejected.");
        }
        _ecdsa = new ECDsaCng(key);
        if (_ecdsa.KeySize != 256)
        {
            _ecdsa.Dispose();
            throw new CryptographicException("AEVRIX device key must be ECDSA P-256.");
        }
        SecurityTierValue = tier;
        PublicJwk = BuildPublicJwk(_ecdsa);
        KeyId = ComputeJwkThumbprint(PublicJwk);
    }

    public string InstallationId { get; }
    public DeviceKeySecurityTier SecurityTierValue { get; }
    public string KeyId { get; }
    public string SecurityTier => SecurityTierValue switch
    {
        DeviceKeySecurityTier.TpmNonExportable => "tpm-non-exportable",
        DeviceKeySecurityTier.SoftwareNonExportable => "software-non-exportable",
        _ => "unknown"
    };
    public AevrixPublicJwk PublicJwk { get; }

    public byte[] ExportSubjectPublicKeyInfo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _ecdsa.ExportSubjectPublicKeyInfo();
    }

    public byte[] SignEs256(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public string CreatePkcs10CsrPem()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN=AEVRIX Device {InstallationId}"),
            _ecdsa,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2", "Client Authentication") },
            critical: false));
        return request.CreateSigningRequestPem();
    }

    public InstalledDeviceCertificate InstallIssuedClientCertificate(ReadOnlySpan<byte> certificateDer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var issued = X509CertificateLoader.LoadCertificate(certificateDer);
        using var issuedPublic = issued.GetECDsaPublicKey()
            ?? throw new CryptographicException("AEVRIX issued device certificate is not ECDSA.");
        if (!CryptographicOperations.FixedTimeEquals(issuedPublic.ExportSubjectPublicKeyInfo(), ExportSubjectPublicKeyInfo()))
        {
            throw new CryptographicException("AEVRIX issued device certificate does not match the local non-exportable key.");
        }
        ValidateIssuedClientCertificateProfile(issued);

        using var bound = issued.CopyWithPrivateKey(_ecdsa);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(bound);
        return new InstalledDeviceCertificate(
            issued.Thumbprint,
            Convert.ToHexString(SHA256.HashData(issued.RawData)).ToLowerInvariant(),
            new DateTimeOffset(issued.NotBefore.ToUniversalTime()),
            new DateTimeOffset(issued.NotAfter.ToUniversalTime()));
    }

    private static void ValidateIssuedClientCertificateProfile(X509Certificate2 issued)
    {
        var now = DateTime.UtcNow;
        if (now < issued.NotBefore.ToUniversalTime() || now >= issued.NotAfter.ToUniversalTime())
        {
            throw new CryptographicException("AEVRIX issued device certificate is expired or not yet valid.");
        }
        var basicConstraints = issued.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (basicConstraints?.CertificateAuthority == true)
        {
            throw new CryptographicException("AEVRIX device certificate must not be a CA certificate.");
        }
        var keyUsage = issued.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (keyUsage is null
            || (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0
            || (keyUsage.KeyUsages & (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign)) != 0)
        {
            throw new CryptographicException("AEVRIX device certificate requires DigitalSignature usage and must not sign certificates/CRLs.");
        }
        var eku = issued.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => string.Equals(oid.Value, "1.3.6.1.5.5.7.3.2", StringComparison.Ordinal)))
        {
            throw new CryptographicException("AEVRIX device certificate requires the TLS client-authentication EKU.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _ecdsa.Dispose();
    }

    internal static AevrixPublicJwk BuildPublicJwk(ECDsa ecdsa)
    {
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        if (!string.Equals(parameters.Curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal)
            || parameters.Q.X is null
            || parameters.Q.Y is null
            || parameters.Q.X.Length != 32
            || parameters.Q.Y.Length != 32)
        {
            throw new CryptographicException("AEVRIX DPoP identity requires an ECDSA P-256 public key.");
        }
        return new AevrixPublicJwk(
            "EC",
            "P-256",
            Base64Url(parameters.Q.X),
            Base64Url(parameters.Q.Y));
    }

    internal static string ComputeJwkThumbprint(AevrixPublicJwk jwk)
    {
        // RFC 7638 member order for an EC JWK: crv, kty, x, y.
        var canonical = $"{{\"crv\":\"{jwk.Crv}\",\"kty\":\"{jwk.Kty}\",\"x\":\"{jwk.X}\",\"y\":\"{jwk.Y}\"}}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ValidateInstallationId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is < 16 or > 64 || normalized.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
        {
            throw new ArgumentException("Installation id contains unsupported characters.", nameof(value));
        }
        return normalized;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceIdentityProvisioner
{
    public WindowsDeviceSigningKey GetOrCreateTpmKey(string installationId)
        => GetOrCreate(installationId, DeviceKeySecurityTier.TpmNonExportable);

    public WindowsDeviceSigningKey GetOrCreateSoftwareKeyExplicitly(string installationId)
        => GetOrCreate(installationId, DeviceKeySecurityTier.SoftwareNonExportable);

    public DeviceEnrollmentMaterial CreateEnrollmentMaterial(WindowsDeviceSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new DeviceEnrollmentMaterial(
            key.InstallationId,
            key.KeyId,
            key.SecurityTierValue,
            key.PublicJwk,
            key.CreatePkcs10CsrPem());
    }

    private static WindowsDeviceSigningKey GetOrCreate(string installationId, DeviceKeySecurityTier tier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AEVRIX Windows device identity requires Windows CNG.");
        }
        var normalized = installationId.Trim().ToLowerInvariant();
        if (normalized.Length is < 16 or > 64 || normalized.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
        {
            throw new ArgumentException("Installation id contains unsupported characters.", nameof(installationId));
        }

        var provider = tier switch
        {
            DeviceKeySecurityTier.TpmNonExportable => CngProvider.MicrosoftPlatformCryptoProvider,
            DeviceKeySecurityTier.SoftwareNonExportable => CngProvider.MicrosoftSoftwareKeyStorageProvider,
            _ => throw new ArgumentOutOfRangeException(nameof(tier))
        };
        var keyName = "AEVRIX.Device." + normalized;

        CngKey key;
        if (CngKey.Exists(keyName, provider))
        {
            key = CngKey.Open(keyName, provider, CngKeyOpenOptions.UserKey);
        }
        else
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = provider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
                KeyCreationOptions = CngKeyCreationOptions.None
            };
            try
            {
                key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, parameters);
            }
            catch (CryptographicException) when (tier == DeviceKeySecurityTier.TpmNonExportable)
            {
                // Deliberately no automatic downgrade. A lower tier requires an explicit caller policy/action.
                throw new InvalidOperationException(
                    "AEVRIX could not create the TPM-backed device key. Software fallback is never automatic.");
            }
        }

        try
        {
            var keyProvider = key.Provider;
            if (keyProvider is null
                || !string.Equals(keyProvider.Provider, provider.Provider, StringComparison.Ordinal)
                || key.ExportPolicy != CngExportPolicies.None
                || (key.KeyUsage & CngKeyUsages.Signing) == 0)
            {
                throw new CryptographicException("Existing AEVRIX device key does not satisfy the required provider/export/signing policy.");
            }
            return new WindowsDeviceSigningKey(normalized, key, tier);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceCertificateProvider
{
    public X509Certificate2 LoadByThumbprint(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        var normalized = new string(thumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length < 40)
        {
            throw new ArgumentException("Device certificate thumbprint is invalid.", nameof(thumbprint));
        }

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        if (matches.Count != 1)
        {
            foreach (var certificate in matches)
            {
                certificate.Dispose();
            }
            throw new InvalidOperationException("AEVRIX device certificate was not found uniquely in CurrentUser\\My.");
        }

        var result = matches[0];
        for (var i = 1; i < matches.Count; i++)
        {
            matches[i].Dispose();
        }
        if (!result.HasPrivateKey)
        {
            result.Dispose();
            throw new InvalidOperationException("AEVRIX device certificate has no private key association.");
        }
        using var key = result.GetECDsaPrivateKey();
        if (key is null || key.KeySize != 256)
        {
            result.Dispose();
            throw new InvalidOperationException("AEVRIX device certificate private key is not ECDSA P-256.");
        }
        var now = DateTime.UtcNow;
        if (now < result.NotBefore.ToUniversalTime() || now >= result.NotAfter.ToUniversalTime())
        {
            result.Dispose();
            throw new InvalidOperationException("AEVRIX device certificate is expired or not yet valid.");
        }
        return result;
    }
}
