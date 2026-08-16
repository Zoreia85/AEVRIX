using System.Security.Cryptography;

namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Preserves replay rejection during HMAC-key rotation by submitting retained-key aliases and the
/// current-key alias to one atomic claim-set persistence operation. Distributed deployments must
/// provide an IAtomicPromotionClaimSetStore backed by a genuinely cross-host atomic primitive.
/// </summary>
public sealed class RotatingKeyedPromotionReplayGuard : IPromotionReplayGuard, IDisposable
{
    private readonly IAtomicPromotionClaimSetStore _claimStore;
    private readonly byte[] _currentKey;
    private readonly byte[][] _previousKeys;
    private bool _disposed;

    public RotatingKeyedPromotionReplayGuard(
        IAtomicPromotionClaimSetStore claimStore,
        ReadOnlySpan<byte> currentKey,
        IEnumerable<byte[]>? previousKeys = null)
    {
        _claimStore = claimStore ?? throw new ArgumentNullException(nameof(claimStore));
        ValidateKey(currentKey, nameof(currentKey));
        _currentKey = currentKey.ToArray();

        var source = previousKeys?.ToArray() ?? [];
        _previousKeys = new byte[source.Length][];
        try
        {
            for (var index = 0; index < source.Length; index++)
            {
                var key = source[index] ?? throw new ArgumentException(
                    "Previous promotion replay keys must not contain null entries.",
                    nameof(previousKeys));
                ValidateKey(key, nameof(previousKeys));
                _previousKeys[index] = key.ToArray();
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(_currentKey);
            foreach (var key in _previousKeys)
            {
                if (key is not null)
                    CryptographicOperations.ZeroMemory(key);
            }
            throw;
        }
    }

    public bool TryClaim(VerifiedPromotionAuthorityAttestation attestation, out string replayKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(attestation);

        var forbiddenAliases = _previousKeys
            .Select(previousKey => PromotionClaimIdDerivation.Derive(attestation, previousKey))
            .ToArray();

        replayKey = PromotionClaimIdDerivation.Derive(attestation, _currentKey);
        return _claimStore.TryCreateIfNoneExist(replayKey, forbiddenAliases);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(_currentKey);
        foreach (var key in _previousKeys)
            CryptographicOperations.ZeroMemory(key);

        _disposed = true;
    }

    private static void ValidateKey(ReadOnlySpan<byte> key, string parameterName)
    {
        if (key.Length < 32)
            throw new ArgumentException("Promotion replay HMAC keys must contain at least 256 bits.", parameterName);
    }
}
