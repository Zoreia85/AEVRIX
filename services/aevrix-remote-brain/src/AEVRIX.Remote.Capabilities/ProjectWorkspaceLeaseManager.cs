using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Capabilities;

public sealed record ProjectWorkspaceLeaseOptions(
    string RootDirectory,
    int MaximumRelativePathLength = 1_024)
{
    public ProjectWorkspaceLeaseOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RootDirectory);
        var fullRoot = Path.GetFullPath(RootDirectory);
        if (!Path.IsPathRooted(fullRoot))
        {
            throw new ArgumentException("Workspace root must be absolute.", nameof(RootDirectory));
        }
        if (MaximumRelativePathLength is < 32 or > 8_192)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRelativePathLength));
        }
        return this with { RootDirectory = fullRoot };
    }
}

public sealed class ProjectWorkspaceLeaseManager
{
    private readonly ProjectWorkspaceLeaseOptions _options;

    public ProjectWorkspaceLeaseManager(ProjectWorkspaceLeaseOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        Directory.CreateDirectory(_options.RootDirectory);
        EnsureNotReparsePoint(_options.RootDirectory);
    }

    public ProjectWorkspaceLease Create(Guid projectId, string workId, AdapterWorkspaceScope workspaceScope)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }
        McpServerDescriptor.ValidateId(workId, nameof(workId));
        if (workspaceScope == AdapterWorkspaceScope.None)
        {
            throw new InvalidOperationException("A filesystem workspace cannot be created for WorkspaceScope.None.");
        }

        var projectBucket = "p-" + HashToken(projectId.ToString("D"), 16);
        var workBucket = "w-" + HashToken(workId, 12);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var projectRoot = Path.Combine(_options.RootDirectory, projectBucket);
        var leaseRoot = Path.Combine(projectRoot, $"{workBucket}-{nonce}");

        Directory.CreateDirectory(projectRoot);
        EnsureNotReparsePoint(projectRoot);
        Directory.CreateDirectory(leaseRoot);
        EnsureNotReparsePoint(leaseRoot);

        return new ProjectWorkspaceLease(
            leaseRoot,
            projectId,
            workId,
            workspaceScope,
            _options.MaximumRelativePathLength);
    }

    private static string HashToken(string value, int hexLength)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant()[..hexLength];
    }

    internal static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Workspace containment root cannot be a filesystem reparse point.");
        }
    }
}

public sealed class ProjectWorkspaceLease : IAsyncDisposable
{
    private readonly int _maximumRelativePathLength;
    private int _disposed;

    internal ProjectWorkspaceLease(
        string rootPath,
        Guid projectId,
        string workId,
        AdapterWorkspaceScope workspaceScope,
        int maximumRelativePathLength)
    {
        RootPath = Path.GetFullPath(rootPath);
        ProjectId = projectId;
        WorkId = workId;
        WorkspaceScope = workspaceScope;
        _maximumRelativePathLength = maximumRelativePathLength;
    }

    public string RootPath { get; }
    public Guid ProjectId { get; }
    public string WorkId { get; }
    public AdapterWorkspaceScope WorkspaceScope { get; }
    public bool CanWrite => WorkspaceScope == AdapterWorkspaceScope.ReadWrite;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public string ResolveRelativePath(string relativePath)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Length > _maximumRelativePathLength
            || relativePath.Any(char.IsControl)
            || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Workspace relative path is invalid.");
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("Workspace traversal segments are forbidden.");
        }

        var candidate = Path.GetFullPath(Path.Combine(RootPath, Path.Combine(segments)));
        var prefix = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison()))
        {
            throw new InvalidDataException("Resolved workspace path escaped the leased root.");
        }

        EnsureExistingParentsAreNotReparsePoints(candidate);
        return candidate;
    }

    public void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!CanWrite)
        {
            throw new UnauthorizedAccessException("This workspace lease is not writable.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        if (Directory.Exists(RootPath))
        {
            DeleteTreeWithoutFollowingReparsePoints(RootPath);
        }
        if (Directory.Exists(RootPath) || File.Exists(RootPath))
        {
            throw new IOException("Ephemeral workspace destruction could not be verified.");
        }
        return ValueTask.CompletedTask;
    }

    private void EnsureExistingParentsAreNotReparsePoints(string candidate)
    {
        ProjectWorkspaceLeaseManager.EnsureNotReparsePoint(RootPath);
        var parent = Directory.Exists(candidate) ? candidate : Path.GetDirectoryName(candidate);
        while (!string.IsNullOrEmpty(parent) && IsWithinRoot(parent))
        {
            if (Directory.Exists(parent))
            {
                ProjectWorkspaceLeaseManager.EnsureNotReparsePoint(parent);
            }
            if (string.Equals(parent, RootPath, PathComparison()))
            {
                break;
            }
            parent = Path.GetDirectoryName(parent);
        }
    }

    private bool IsWithinRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return string.Equals(full, RootPath, PathComparison()) || full.StartsWith(prefix, PathComparison());
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void DeleteTreeWithoutFollowingReparsePoints(string directory)
    {
        var info = new DirectoryInfo(directory);
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry.FullName, recursive: false);
                }
                else
                {
                    File.Delete(entry.FullName);
                }
                continue;
            }
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                DeleteTreeWithoutFollowingReparsePoints(entry.FullName);
                continue;
            }
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(entry.FullName, entry.Attributes & ~FileAttributes.ReadOnly);
            }
            File.Delete(entry.FullName);
        }
        Directory.Delete(directory, recursive: false);
    }
}
