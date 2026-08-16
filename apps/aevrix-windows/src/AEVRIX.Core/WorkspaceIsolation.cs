using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Core;

/// <summary>
/// Derives privacy-preserving, workspace-scoped local storage paths.
/// Raw user/workspace identifiers are never embedded in paths.
/// </summary>
public sealed class WorkspaceIsolation
{
    private const int OpaqueIdBytes = 16;
    private readonly string _root;

    public WorkspaceIsolation(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    public string UserRoot(string userId) =>
        ContainedPath(_root, "users", OpaqueId("usr", userId));

    public string WorkspaceRoot(string userId, string workspaceId) =>
        ContainedPath(UserRoot(userId), "workspaces", OpaqueId("wsp", workspaceId));

    public string EvidenceRoot(string userId, string workspaceId) =>
        ContainedPath(WorkspaceRoot(userId, workspaceId), "evidence");

    public string BlueprintRoot(string userId, string workspaceId) =>
        ContainedPath(WorkspaceRoot(userId, workspaceId), "blueprint");

    public string VaultRoot(string userId, string workspaceId) =>
        ContainedPath(WorkspaceRoot(userId, workspaceId), "vault");

    public string ResolveWorkspaceFile(string userId, string workspaceId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Workspace paths must be relative.", nameof(relativePath));
        }

        var workspaceRoot = WorkspaceRoot(userId, workspaceId);
        var candidate = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        EnsureContained(workspaceRoot, candidate);
        return candidate;
    }

    public static string OpaqueId(string purpose, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalizedPurpose = purpose.Trim().ToLowerInvariant();
        var normalizedValue = value.Trim();
        var payload = Encoding.UTF8.GetBytes($"aevrix:{normalizedPurpose}\n{normalizedValue}");
        var digest = SHA256.HashData(payload);
        return Convert.ToHexString(digest.AsSpan(0, OpaqueIdBytes)).ToLowerInvariant();
    }

    private static string ContainedPath(string root, params string[] segments)
    {
        var candidate = Path.GetFullPath(segments.Aggregate(root, Path.Combine));
        EnsureContained(root, candidate);
        return candidate;
    }

    private static void EnsureContained(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!normalizedCandidate.StartsWith(normalizedRoot, comparison))
        {
            throw new InvalidOperationException("Resolved path escapes the isolated workspace boundary.");
        }
    }
}
