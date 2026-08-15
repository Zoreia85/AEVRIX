using System.Security.Cryptography;

namespace Aevrix.Remote.Orchestration;

public sealed record PromotionAuthorityVerifierOptions(
    string ExpectedSigningKeyId,
    string SigningPublicKeyPem,
    TimeSpan MaximumAttestationLifetime,
    TimeSpan MaximumFutureSkew)
{
    public static PromotionAuthorityVerifierOptions CreateDefault(
        string expectedSigningKeyId,
        string signingPublicKeyPem) =>
        new(
            expectedSigningKeyId,
            signingPublicKeyPem,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromSeconds(30));

    public PromotionAuthorityVerifierOptions Validate()
    {
        RemoteExecutionAuthorityClientOptions.ValidateToken(
            ExpectedSigningKeyId,
            nameof(ExpectedSigningKeyId),
            3,
            120);
        ArgumentException.ThrowIfNullOrWhiteSpace(SigningPublicKeyPem);
        if (MaximumAttestationLifetime < TimeSpan.FromSeconds(30)
            || MaximumAttestationLifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttestationLifetime));
        }
        if (MaximumFutureSkew < TimeSpan.Zero || MaximumFutureSkew > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFutureSkew));
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(SigningPublicKeyPem);
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException(
                "Execution Authority public signing key is invalid.",
                nameof(SigningPublicKeyPem),
                exception);
        }
        if (key.KeySize != 256)
        {
            throw new ArgumentException(
                "Execution Authority signing key must be ECDSA P-256.",
                nameof(SigningPublicKeyPem));
        }
        return this;
    }
}

public sealed record VerifiedPromotionAuthorityAttestation(
    Guid ProjectId,
    string RunId,
    string ExecutionId,
    string EvidenceDigestSha256,
    ExecutionProofHead LedgerHead,
    string KeyId,
    long IssuedAtUnixSeconds,
    long ExpiresAtUnixSeconds,
    string Nonce,
    string PublicKeySpkiSha256);

/// <summary>
/// Pure verifier for queue-side or release-side promotion authorization. It requires only public
/// verification material and the canonical promotion evidence; it has no network or HMAC-client
/// dependency and therefore can run in a trust boundary separate from the Authority client.
/// </summary>
public sealed class PromotionAuthorityAttestationVerifier
{
    private readonly PromotionAuthorityVerifierOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _publicKeySpkiSha256;

    public PromotionAuthorityAttestationVerifier(
        PromotionAuthorityVerifierOptions options,
        TimeProvider? timeProvider = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;

        using var key = ECDsa.Create();
        key.ImportFromPem(_options.SigningPublicKeyPem);
        _publicKeySpkiSha256 = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
    }

    public VerifiedPromotionAuthorityAttestation Verify(
        PromotionAuthorityAttestation attestation,
        PromotionEvidenceEnvelope evidence)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateEvidence(evidence);
        attestation.ValidateStructural();

        var evidenceDigest = evidence.ComputeDigestSha256();
        if (!string.Equals(attestation.KeyId, _options.ExpectedSigningKeyId, StringComparison.Ordinal)
            || attestation.ProjectId != evidence.ProjectId
            || !string.Equals(attestation.RunId, evidence.RunId, StringComparison.Ordinal)
            || !string.Equals(attestation.ExecutionId, evidence.ExecutionId, StringComparison.Ordinal)
            || attestation.HeadEntryCount != evidence.LedgerHead.EntryCount
            || !CryptographicHexEquals(attestation.HeadHashSha256, evidence.LedgerHead.HeadHashSha256)
            || !CryptographicHexEquals(attestation.EvidenceDigestSha256, evidenceDigest)
            || !CryptographicHexEquals(
                attestation.PublicKeySpkiSha256,
                Convert.ToHexString(_publicKeySpkiSha256)))
        {
            throw new InvalidDataException(
                "Execution Authority attestation is not bound to the exact promotion evidence and pinned public key.");
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var maximumFutureSkew = checked((long)_options.MaximumFutureSkew.TotalSeconds);
        var maximumLifetime = checked((long)_options.MaximumAttestationLifetime.TotalSeconds);
        if (attestation.IssuedAtUnixSeconds > now + maximumFutureSkew
            || attestation.ExpiresAtUnixSeconds < now
            || attestation.ExpiresAtUnixSeconds - attestation.IssuedAtUnixSeconds > maximumLifetime)
        {
            throw new InvalidDataException(
                "Execution Authority attestation is expired, future-dated or exceeds the configured lifetime.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(attestation.SignatureDerBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Execution Authority attestation signature is not valid Base64.",
                exception);
        }

        var payload = attestation.CanonicalPayloadUtf8();
        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportFromPem(_options.SigningPublicKeyPem);
            if (!publicKey.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new InvalidDataException(
                    "Execution Authority attestation signature verification failed.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(payload);
        }

        return new VerifiedPromotionAuthorityAttestation(
            attestation.ProjectId,
            attestation.RunId,
            attestation.ExecutionId,
            evidenceDigest,
            new ExecutionProofHead(attestation.HeadEntryCount, attestation.HeadHashSha256.ToLowerInvariant()),
            attestation.KeyId,
            attestation.IssuedAtUnixSeconds,
            attestation.ExpiresAtUnixSeconds,
            attestation.Nonce,
            attestation.PublicKeySpkiSha256.ToLowerInvariant());
    }

    private static void ValidateEvidence(PromotionEvidenceEnvelope evidence)
    {
        if (evidence.Version != ExecutionProofLedger.CurrentVersion)
        {
            throw new InvalidDataException("Promotion evidence version is unsupported.");
        }
        if (evidence.ProjectId == Guid.Empty)
        {
            throw new InvalidDataException("Promotion evidence project id is empty.");
        }
        ExecutionProofEvent.ValidateSafeId(evidence.RunId, nameof(evidence.RunId), 3, 160);
        ExecutionProofEvent.ValidateSafeId(evidence.ExecutionId, nameof(evidence.ExecutionId), 3, 160);
        ExecutionProofEvent.ValidateSafeId(evidence.CapabilityClass, nameof(evidence.CapabilityClass), 2, 80);
        ExecutionProofEvent.ValidateSafeId(evidence.CapabilityId, nameof(evidence.CapabilityId), 2, 160);
        ExecutionProofEvent.ValidateSha256(evidence.ArtifactManifestSha256, nameof(evidence.ArtifactManifestSha256), required: true);
        ExecutionProofEvent.ValidateSha256(evidence.ValidationDigestSha256, nameof(evidence.ValidationDigestSha256), required: true);
        ExecutionProofEvent.ValidateSha256(evidence.JudgeDecisionDigestSha256, nameof(evidence.JudgeDecisionDigestSha256), required: true);
        ExecutionProofEvent.ValidateSha256(evidence.PromotionDigestSha256, nameof(evidence.PromotionDigestSha256), required: true);
        ExecutionProofEvent.ValidateSha256(evidence.AuthorizationRecordHashSha256, nameof(evidence.AuthorizationRecordHashSha256), required: true);
        ExecutionProofEvent.ValidateSha256(evidence.LedgerHead.HeadHashSha256, nameof(evidence.LedgerHead.HeadHashSha256), required: true);
        if (evidence.LedgerHead.EntryCount <= 0)
        {
            throw new InvalidDataException("Promotion evidence ledger head is invalid.");
        }
        if (!CryptographicHexEquals(
                evidence.AuthorizationRecordHashSha256,
                evidence.LedgerHead.HeadHashSha256))
        {
            throw new InvalidDataException(
                "Promotion authorization record must be the exact anchored ledger head.");
        }
    }

    private static bool CryptographicHexEquals(string left, string right)
    {
        if (left.Length != right.Length || left.Length != 64) return false;
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            try
            {
                return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(leftBytes);
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
