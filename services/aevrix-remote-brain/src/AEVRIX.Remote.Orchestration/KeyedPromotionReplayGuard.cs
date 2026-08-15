using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Atomic persistence boundary for opaque promotion claim identifiers. Implementations must
/// provide compare-and-set semantics: exactly one caller may create a given identifier.
/// </summary>
public interface IPromotionClaimStore
{
    bool TryCreate(string opaqueClaimId);
}

/// <summary>
/// Local durable claim store. The filename is already an opaque keyed identifier; the store never
/// receives project, run, execution or evidence identifiers in plaintext.
/// </summary>
public sealed class FilePromotionClaimStore : IPromotionClaimStore
{
    private static readonly byte[] ClaimMarker = Encoding.ASCII.GetBytes("AEVRIX_OPAQUE_PROMOTION_CLAIM_V1\n");
    private readonly string _claimDirectory;

    public FilePromotionClaimStore(string claimDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimDirectory);
        _claimDirectory = Path.GetFullPath(claimDirectory);
        Directory.CreateDirectory(_claimDirectory);
        RejectReparsePoint(_claimDirectory);
    }

    public bool TryCreate(string opaqueClaimId)
    {
        ValidateOpaqueClaimId(opaqueClaimId);
        RejectReparsePoint(_claimDirectory);

        var claimPath = Path.Combine(_claimDirectory, opaqueClaimId + ".claim");
        try
        {
            using var stream = new FileStream(
                claimPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.Write(ClaimMarker);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException) when (File.Exists(claimPath))
        {
            return false;
        }
    }

    private static void ValidateOpaqueClaimId(string opaqueClaimId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opaqueClaimId);
        if (opaqueClaimId.Length != 64 ||
            opaqueClaimId.Any(static value => !char.IsAsciiHexDigit(value) || char.IsUpper(value)))
        {
            throw new ArgumentException("Promotion claim identifier must be 64 lowercase hexadecimal characters.", nameof(opaqueClaimId));
        }
    }

    private static void RejectReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Promotion claim directory must not be a symbolic link or reparse point.");
        }
    }
}

/// <summary>
/// Privacy-preserving replay guard. It derives a deterministic HMAC-SHA-256 claim identifier from
/// the canonical promotion identity before crossing the persistence boundary. This prevents an
/// observer of the claim store from testing guessed project/run/execution identities without the
/// deployment key. The Authority nonce is intentionally excluded by the canonical identity, so a
/// second valid signature over the same promotion cannot bypass the claim.
/// </summary>
public sealed class KeyedPromotionReplayGuard : IPromotionReplayGuard, IDisposable
{
    private const string DomainSeparator = "AEVRIX_PROMOTION_CLAIM_HMAC_V1\n";
    private readonly IPromotionClaimStore _claimStore;
    private readonly byte[] _key;
    private bool _disposed;

    public KeyedPromotionReplayGuard(IPromotionClaimStore claimStore, ReadOnlySpan<byte> key)
    {
        _claimStore = claimStore ?? throw new ArgumentNullException(nameof(claimStore));
        if (key.Length < 32)
        {
            throw new ArgumentException("Promotion replay HMAC key must contain at least 256 bits.", nameof(key));
        }

        _key = key.ToArray();
    }

    public bool TryClaim(VerifiedPromotionAuthorityAttestation attestation, out string replayKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(attestation);

        var canonicalIdentity = InMemoryPromotionReplayGuard.BuildReplayKey(attestation);
        var payload = Encoding.UTF8.GetBytes(DomainSeparator + canonicalIdentity);
        try
        {
            replayKey = Convert.ToHexString(HMACSHA256.HashData(_key, payload)).ToLowerInvariant();
            return _claimStore.TryCreate(replayKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}
