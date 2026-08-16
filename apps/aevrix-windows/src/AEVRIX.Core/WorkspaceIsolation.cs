using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Core;

public sealed class WorkspaceDataPaths
{
    private readonly string _workspaceRoot;

    public WorkspaceDataPaths(AevrixDataPaths paths, WorkspaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();

        Scope = scope;
        RootPaths = paths.EnsureCreated();
        ScopeBindingSha256 = ComputeScopeBinding(scope);

        var userSegment = OpaqueSegment("user", scope.UserId);
        var workspaceSegment = OpaqueSegment("workspace", scope.WorkspaceId);
        _workspaceRoot = Path.GetFullPath(Path.Combine(RootPaths.UserRoot, "Workspaces", userSegment, workspaceSegment));

        ProjectsRoot = Path.Combine(_workspaceRoot, "Projects");
        VaultRoot = Path.Combine(_workspaceRoot, "Vault", OpaqueSegment("encryption", scope.EncryptionContextId));
        BrowserProfilesRoot = Path.Combine(_workspaceRoot, "BrowserProfiles");
        LogsRoot = Path.Combine(_workspaceRoot, "Logs");
        CacheRoot = Path.Combine(_workspaceRoot, "Cache");
    }

    public WorkspaceScope Scope { get; }
    public AevrixDataPaths RootPaths { get; }
    public string ScopeBindingSha256 { get; }
    public string WorkspaceRoot => _workspaceRoot;
    public string ProjectsRoot { get; }
    public string VaultRoot { get; }
    public string BrowserProfilesRoot { get; }
    public string LogsRoot { get; }
    public string CacheRoot { get; }

    public WorkspaceDataPaths EnsureCreated()
    {
        foreach (var path in new[] { WorkspaceRoot, ProjectsRoot, VaultRoot, BrowserProfilesRoot, LogsRoot, CacheRoot })
        {
            Directory.CreateDirectory(path);
        }
        return this;
    }

    public string ProjectRoot(Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }
        return Path.Combine(ProjectsRoot, projectId.ToString("D"));
    }

    public string ProjectEvidenceRoot(Guid projectId) => Path.Combine(ProjectRoot(projectId), "evidence");
    public string ProjectBlueprintRoot(Guid projectId) => Path.Combine(ProjectRoot(projectId), "blueprint");

    public string ResolveWorkspaceRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Workspace-relative paths cannot be rooted.");
        }

        var resolved = Path.GetFullPath(Path.Combine(WorkspaceRoot, relativePath));
        if (!IsContained(WorkspaceRoot, resolved))
        {
            throw new InvalidOperationException("Path escapes the workspace boundary.");
        }
        return resolved;
    }

    public bool Contains(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return IsContained(WorkspaceRoot, Path.GetFullPath(path));
    }

    private static string ComputeScopeBinding(WorkspaceScope scope)
    {
        var canonical = string.Join('\n', new[] { "AEVRIX-WORKSPACE-SCOPE-V1", scope.UserId, scope.WorkspaceId, scope.EncryptionContextId });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string OpaqueSegment(string dimension, string value)
    {
        var canonical = Encoding.UTF8.GetBytes($"AEVRIX-PATH-SEGMENT-V1\n{dimension}\n{value}");
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()[..32];
    }

    private static bool IsContained(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record WorkspaceEncryptedEnvelope(byte[] Nonce, byte[] Ciphertext, byte[] Tag, string ScopeBindingSha256, string Purpose);

public sealed class WorkspaceEnvelopeEncryption
{
    private const int MinimumMasterKeyBytes = 32;
    private readonly WorkspaceDataPaths _workspace;

    public WorkspaceEnvelopeEncryption(WorkspaceDataPaths workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public WorkspaceEncryptedEnvelope Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> masterKey, string purpose)
    {
        ValidateMasterKey(masterKey);
        WorkspaceScope.ValidateToken(purpose, nameof(purpose));

        var derivedKey = DeriveWorkspaceKey(masterKey, purpose);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var associatedData = AssociatedData(purpose);

        try
        {
            using var aes = new AesGcm(derivedKey, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return new WorkspaceEncryptedEnvelope(nonce, ciphertext, tag, _workspace.ScopeBindingSha256, purpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    public byte[] Decrypt(WorkspaceEncryptedEnvelope envelope, ReadOnlySpan<byte> masterKey, string purpose)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateMasterKey(masterKey);
        WorkspaceScope.ValidateToken(purpose, nameof(purpose));

        if (!string.Equals(envelope.ScopeBindingSha256, _workspace.ScopeBindingSha256, StringComparison.Ordinal)
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
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, AssociatedData(purpose));
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
        var context = Encoding.UTF8.GetBytes(string.Join('\n', new[] { "AEVRIX-WORKSPACE-KEY-V1", _workspace.ScopeBindingSha256, purpose }));
        using var hmac = new HMACSHA256(masterKey.ToArray());
        return hmac.ComputeHash(context);
    }

    private byte[] AssociatedData(string purpose) => Encoding.UTF8.GetBytes(string.Join('\n', new[] { "AEVRIX-WORKSPACE-AAD-V1", _workspace.ScopeBindingSha256, purpose }));

    private static void ValidateMasterKey(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length < MinimumMasterKeyBytes)
        {
            throw new ArgumentException($"Workspace master key must contain at least {MinimumMasterKeyBytes} bytes.", nameof(masterKey));
        }
    }
}
