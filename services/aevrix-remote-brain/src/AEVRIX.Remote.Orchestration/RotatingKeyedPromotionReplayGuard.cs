using System.Security.Cryptography;

namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Replay guard for coordinated HMAC-key rotation. Claims are written under the current key while
/// all configured legacy-key aliases are checked before creation, so promotions claimed before a
/// rotation remain blocked after the rotation. The lookup store receives only opaque HMAC values.
/// </summary>
public sealed class RotatingKeyedPromotionReplayGuard : IPromotionReplayGuard, IDisposable
{
    private readonly IPromotionClaimLookupStore _claimStore;
    private readonly byte[] _currentKey;
    private readonly byte[][] _legacyKeys;
    private bool _disposed;

    public RotatingKeyedPromotionReplayGuard(
        IPromotionClaimLookupStore claimStore,
        ReadOnlySpan<byte> currentKey,
        IEnumerable<byte[]>? legacyKeys = null)
    {
        _claimStore = claimStore ?? throw new ArgumentNullException(nameof(claimStore));
        KeyedPromotionReplayGuard.ValidateKey(currentKey, nameof(currentKey));
        _currentKey = currentKey.ToArray();

        _legacyKeys = (legacyKeys ?? Array.Empty<byte[]>())
            .Select(static key => key?.ToArray() ?? throw new ArgumentException("Legacy promotion replay key cannot be null."))
            .ToArray();

        foreach (var legacyKey in _legacyKeys)
        {
            KeyedPromotionReplayGuard.ValidateKey(legacyKey, nameof(legacyKeys));
        }
    }

    public bool TryClaim(VerifiedPromotionAuthorityAttestation attestation, out string replayKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(attestation);

        foreach (var legacyKey in _legacyKeys)
        {
            var legacyClaimId = KeyedPromotionReplayGuard.DeriveClaimId(attestation, legacyKey);
            if (_claimStore.Exists(legacyClaimId))
            {
                replayKey = legacyClaimId;
                return false;
            }
        }

        replayKey = KeyedPromotionReplayGuard.DeriveClaimId(attestation, _currentKey);
        return _claimStore.TryCreate(replayKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_currentKey);
        foreach (var legacyKey in _legacyKeys)
        {
            CryptographicOperations.ZeroMemory(legacyKey);
        }

        _disposed = true;
    }
}
