using System.Security.Cryptography;

namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Preserves replay rejection during a coordinated HMAC-key rotation by checking aliases produced
/// by retained previous keys before creating the current-key alias. This is safe for coordinated
/// rotation against one lookup-capable claim store. Mixed-version multi-host rollout still requires
/// deployment orchestration so all writers recognize the same retained-key window.
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
