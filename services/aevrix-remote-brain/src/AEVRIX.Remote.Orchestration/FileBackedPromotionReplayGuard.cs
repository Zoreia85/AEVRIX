using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Durable local-filesystem implementation of <see cref="IPromotionReplayGuard"/>.
/// Claims are represented only by a SHA-256 digest of the canonical promotion identity, so
/// project/run/execution identifiers are not persisted in plaintext. FileMode.CreateNew is the
/// cross-process compare-and-set primitive: exactly one contender can create a given claim file.
/// </summary>
public sealed class FileBackedPromotionReplayGuard : IPromotionReplayGuard
{
    private const string ClaimPrefix = "promotion-claim-v1:";
    private static readonly byte[] ClaimMarker = Encoding.ASCII.GetBytes("AEVRIX_PROMOTION_CLAIM_V1\n");

    private readonly string _claimDirectory;

    public FileBackedPromotionReplayGuard(string claimDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimDirectory);
        _claimDirectory = Path.GetFullPath(claimDirectory);
        Directory.CreateDirectory(_claimDirectory);
        RejectReparsePoint(_claimDirectory);
    }

    public bool TryClaim(VerifiedPromotionAuthorityAttestation attestation, out string replayKey)
    {
        ArgumentNullException.ThrowIfNull(attestation);

        replayKey = InMemoryPromotionReplayGuard.BuildReplayKey(attestation);
        var claimId = ComputeClaimId(replayKey);
        var claimPath = Path.Combine(_claimDirectory, claimId + ".claim");

        RejectReparsePoint(_claimDirectory);

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

    internal static string ComputeClaimId(string replayKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayKey);
        var payload = Encoding.UTF8.GetBytes(ClaimPrefix + replayKey);
        try
        {
            return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
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
