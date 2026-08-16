using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Core;

/// <summary>
/// Provides a fail-closed local filesystem boundary for one AEVRIX workspace scope.
/// Scope identifiers are never embedded verbatim in storage paths.
/// </summary>
public sealed class WorkspaceStorageBoundary
{
    private readonly string _root;

    public WorkspaceStorageBoundary(string storageRoot, WorkspaceScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();

        Scope = scope;
        var baseRoot = Path.GetFullPath(storageRoot);
        Directory.CreateDirectory(baseRoot);
        RejectReparsePoint(baseRoot);

        _root = Path.Combine(
            baseRoot,
            "u-" + HashToken(scope.UserId),
            "w-" + HashToken(scope.WorkspaceId),
            "e-" + HashToken(scope.EncryptionContextId));

        Directory.CreateDirectory(_root);
        ValidateExistingPathChain(baseRoot, _root);
    }

    public WorkspaceScope Scope { get; }

    public string RootPath => _root;

    public string ResolvePath(string relativePath)
    {
        ValidateRelativePath(relativePath);

        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(_root, normalized));
        EnsureContained(candidate);
        ValidateExistingPathChain(_root, candidate);
        return candidate;
    }

    public string CreateDirectory(string relativePath)
    {
        var path = ResolvePath(relativePath);
        Directory.CreateDirectory(path);
        ValidateExistingPathChain(_root, path);
        return path;
    }

    public FileStream OpenRead(string relativePath)
    {
        var path = ResolvePath(relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Workspace file was not found.", path);
        }

        RejectReparsePoint(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public FileStream OpenWrite(string relativePath, bool overwrite = false)
    {
        var path = ResolvePath(relativePath);
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Workspace file path has no parent directory.");
        Directory.CreateDirectory(parent);
        ValidateExistingPathChain(_root, parent);

        if (File.Exists(path))
        {
            RejectReparsePoint(path);
        }

        return new FileStream(
            path,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
    }

    private void EnsureContained(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException("Workspace path escaped its local storage boundary.");
        }
    }

    private static void ValidateRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException("Absolute paths are not permitted inside a workspace boundary.");
        }

        var parts = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            throw new UnauthorizedAccessException("Workspace-relative paths cannot contain traversal segments.");
        }
    }

    private static void ValidateExistingPathChain(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException("Workspace path escaped its local storage boundary.");
        }

        RejectReparsePoint(fullRoot);
        var current = fullRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) || File.Exists(current))
            {
                RejectReparsePoint(current);
            }
            else
            {
                break;
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Reparse points and symbolic links are not allowed in workspace storage boundaries.");
        }
    }

    private static string HashToken(string token)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }
}
