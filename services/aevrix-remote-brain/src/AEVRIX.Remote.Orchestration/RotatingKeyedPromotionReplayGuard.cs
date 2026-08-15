using System.Security.Cryptography;

namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Rotation-safe privacy-preserving replay guard. Before creating a claim with the current key,
/// it checks opaque aliases derived from previous keys. This preserves replay protection across a
/// coordinated key rotation without exposing canonical promotion identity to the persistence layer.
/// </summary>
public sealed class RotatingKeyedPromotionReplayGuard : IPromotionReplayGuard, IDisposable
{
    private readonly IPromotionClaimLookupStore _claimStore;
    private readonly byte[] _currentKey;
    private readonly byte[][] _previousKeys;
    private bool _disposed;

    public RotatingKeyedPromotionReplayGuard(
        IPromotionClaimLookupStore claimStore,
        ReadOnlySpan<byte> currentKey,
        IEnumerable<byte[]>? previousKeys = null)
    {
        _claimStore = claimStore ?? throw new ArgumentNullException(nameof(claimStore));
        ValidateKey(currentKey, nameof(currentKey));
        _currentKey = currentKey.ToArray();

        _previousKeys = (previousKeys ?? Array.Empty<byte[]>())
            .Select(static key => key?.ToArray() ?? throw new ArgumentException("Previous promotion replay keys must not contain null entries.", nameof(previousKeys)))
            .ToArray();

        try
        {
            foreach (var key in _previousKeys)
            {
                ValidateKey(key, nameof(previousKeys));
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(_currentKey);
            foreach (var key in _previousKeys)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            throw;
        }
    }

    public bool TryClaim(VerifiedPromotionAuthorityAttestation attestation, out string replayKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(attestation);

        foreach (var previousKey in _previousKeys)
        {
            var previousAlias = PromotionClaimIdDerivation.Derive(attestation, previousKey);
            if (_claimStore.Exists(previousAlias))
            {
                replayKey = previousAlias;
                return false;
            }
        }

        replayKey = PromotionClaimIdDerivation.Derive(attestation, _currentKey);
        return _claimStore.TryCreate(replayKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_currentKey);
        foreach (var key in _previousKeys)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _disposed = true;
    }

    private static void ValidateKey(ReadOnlySpan<byte> key, string parameterName)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException("Promotion replay HMAC keys must contain at least 256 bits.", parameterName);
        }
    }
}
