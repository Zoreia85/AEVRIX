using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Core;

public sealed record WorkspaceEncryptedEnvelope(
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag,
    string ScopeBindingSha256,
    string Purpose);

/// <summary>
/// Provides authenticated envelope encryption bound to one user/workspace/encryption context.
/// A ciphertext produced for one scope or purpose cannot be decrypted through another scope.
/// </summary>
public sealed class WorkspaceEnvelopeEncryption
{
    private const int MinimumMasterKeyBytes = 32;
    private readonly WorkspaceScope _scope;
    private readonly string _scopeBindingSha256;

    public WorkspaceEnvelopeEncryption(WorkspaceScope scope)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _scope.Validate();
        _scopeBindingSha256 = ComputeScopeBinding(scope);
    }

    public string ScopeBindingSha256 => _scopeBindingSha256;

    public WorkspaceEncryptedEnvelope Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> masterKey,
        string purpose)
    {
        ValidateMasterKey(masterKey);
        WorkspaceScope.ValidateToken(purpose, nameof(purpose));

        var derivedKey = DeriveWorkspaceKey(masterKey, purpose);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        try
        {
            using var aes = new AesGcm(derivedKey, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(purpose));
            return new WorkspaceEncryptedEnvelope(
                nonce,
                ciphertext,
                tag,
                _scopeBindingSha256,
                purpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    public byte[] Decrypt(
        WorkspaceEncryptedEnvelope envelope,
        ReadOnlySpan<byte> masterKey,
        string purpose)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateMasterKey(masterKey);
        WorkspaceScope.ValidateToken(purpose, nameof(purpose));

        if (!string.Equals(envelope.ScopeBindingSha256, _scopeBindingSha256, StringComparison.Ordinal)
            || !string.Equals(envelope.Purpose, purpose, StringComparison.Ordinal))
        {
            throw new CryptographicException("Encrypted envelope does not belong to this workspace encryption context.");
        }

        if (envelope.Nonce.Length != 12 || envelope.Tag.Length != 16)
        {
            throw new CryptographicException("Encrypted envelope parameters are invalid.");
        }

        var derivedKey = DeriveWorkspaceKey(masterKey, purpose);
        var plaintext = new byte[envelope.Ciphertext.Length];

        try
        {
            using var aes = new AesGcm(derivedKey, envelope.Tag.Length);
            aes.Decrypt(
                envelope.Nonce,
                envelope.Ciphertext,
                envelope.Tag,
                plaintext,
                AssociatedData(purpose));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    private byte[] DeriveWorkspaceKey(ReadOnlySpan<byte> masterKey, string purpose)
    {
        var context = Encoding.UTF8.GetBytes(string.Join('\n', new[]
        {
            "AEVRIX-WORKSPACE-KEY-V1",
            _scopeBindingSha256,
            purpose
        }));

        var keyMaterial = masterKey.ToArray();
        try
        {
            using var hmac = new HMACSHA256(keyMaterial);
            return hmac.ComputeHash(context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial);
        }
    }

    private byte[] AssociatedData(string purpose) => Encoding.UTF8.GetBytes(string.Join('\n', new[]
    {
        "AEVRIX-WORKSPACE-AAD-V1",
        _scopeBindingSha256,
        purpose
    }));

    private static string ComputeScopeBinding(WorkspaceScope scope)
    {
        var canonical = string.Join('\n', new[]
        {
            "AEVRIX-WORKSPACE-SCOPE-V1",
            scope.UserId,
            scope.WorkspaceId,
            scope.EncryptionContextId
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void ValidateMasterKey(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length < MinimumMasterKeyBytes)
        {
            throw new ArgumentException(
                $"Workspace master key must contain at least {MinimumMasterKeyBytes} bytes.",
                nameof(masterKey));
        }
    }
}
