using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Core;

public sealed class WorkspaceStorageBoundary
{
    private readonly string _root;

    public WorkspaceStorageBoundary(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public string ResolveWorkspaceRoot(WorkspaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();

        var userSegment = StableSegment(scope.UserId);
        var workspaceSegment = StableSegment(scope.WorkspaceId);
        var encryptionSegment = StableSegment(scope.EncryptionContextId);

        return EnsureInsideRoot(Path.Combine(
            _root,
            "users",
            userSegment,
            "workspaces",
            workspaceSegment,
            encryptionSegment));
    }

    public string ResolveArtifactPath(
        WorkspaceScope scope,
        string category,
        string artifactName)
    {
        ValidateLeafToken(category, nameof(category));
        ValidateLeafToken(artifactName, nameof(artifactName));

        var workspaceRoot = ResolveWorkspaceRoot(scope);
        return EnsureInsideRoot(Path.Combine(workspaceRoot, category, artifactName));
    }

    public void AssertSameScope(WorkspaceScope expected, WorkspaceScope actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        expected.Validate();
        actual.Validate();

        if (!string.Equals(expected.UserId, actual.UserId, StringComparison.Ordinal)
            || !string.Equals(expected.WorkspaceId, actual.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(expected.EncryptionContextId, actual.EncryptionContextId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workspace storage access crossed the active user/workspace/encryption boundary.");
        }
    }

    private string EnsureInsideRoot(string candidate)
    {
        var full = Path.GetFullPath(candidate);
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved storage path escaped the configured AEVRIX workspace root.");
        }

        return full;
    }

    private static string StableSegment(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest).ToLowerInvariant()[..32];
    }

    private static void ValidateLeafToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Storage path components must be single safe filename tokens.", parameterName);
        }
    }
}
