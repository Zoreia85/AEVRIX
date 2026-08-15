using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class ProjectWorkspaceLeaseManagerTests
{
    [TestMethod]
    public async Task Create_SegregatesProjectsWithoutPlaintextIdentifiers()
    {
        var root = NewRoot();
        try
        {
            var manager = new ProjectWorkspaceLeaseManager(new(root));
            var projectA = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var projectB = Guid.Parse("22222222-2222-2222-2222-222222222222");
            await using var leaseA = manager.Create(projectA, "work-same", AdapterWorkspaceScope.ReadWrite);
            await using var leaseB = manager.Create(projectB, "work-same", AdapterWorkspaceScope.ReadWrite);

            Assert.AreNotEqual(leaseA.RootPath, leaseB.RootPath);
            Assert.IsFalse(leaseA.RootPath.Contains(projectA.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(leaseB.RootPath.Contains(projectB.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(Path.GetFileName(leaseA.RootPath).Contains("work-same", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteIfPresent(root);
        }
    }

    [TestMethod]
    public async Task ResolveRelativePath_RejectsTraversalAndAbsolutePaths()
    {
        var root = NewRoot();
        try
        {
            var manager = new ProjectWorkspaceLeaseManager(new(root));
            await using var lease = manager.Create(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "work-paths",
                AdapterWorkspaceScope.ReadWrite);

            var safe = lease.ResolveRelativePath("artifacts/report.json");
            Assert.IsTrue(safe.StartsWith(lease.RootPath, PathComparison()));
            Assert.Throws<InvalidDataException>(() => lease.ResolveRelativePath("../outside.txt"));
            Assert.Throws<InvalidDataException>(() => lease.ResolveRelativePath("nested/../../outside.txt"));
            Assert.Throws<InvalidDataException>(() => lease.ResolveRelativePath(
                Path.GetFullPath(Path.Combine(root, "outside.txt"))));
        }
        finally
        {
            DeleteIfPresent(root);
        }
    }

    [TestMethod]
    public async Task ReadOnlyLease_RejectsWriteAuthority()
    {
        var root = NewRoot();
        try
        {
            var manager = new ProjectWorkspaceLeaseManager(new(root));
            await using var lease = manager.Create(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "work-readonly",
                AdapterWorkspaceScope.ReadOnly);

            Assert.IsFalse(lease.CanWrite);
            Assert.Throws<UnauthorizedAccessException>(lease.EnsureWritable);
        }
        finally
        {
            DeleteIfPresent(root);
        }
    }

    [TestMethod]
    public async Task DisposeAsync_DeletesEphemeralWorkspaceAndContents()
    {
        var root = NewRoot();
        try
        {
            var manager = new ProjectWorkspaceLeaseManager(new(root));
            var lease = manager.Create(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "work-destroy",
                AdapterWorkspaceScope.ReadWrite);

            var artifact = lease.ResolveRelativePath("nested/evidence.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            await File.WriteAllTextAsync(artifact, "project-confidential-test-material");
            var leasedRoot = lease.RootPath;

            await lease.DisposeAsync();

            Assert.IsTrue(lease.IsDisposed);
            Assert.IsFalse(Directory.Exists(leasedRoot));
            Assert.Throws<ObjectDisposedException>(() => lease.ResolveRelativePath("other.txt"));
        }
        finally
        {
            DeleteIfPresent(root);
        }
    }

    [TestMethod]
    public void Create_RejectsFilesystemWorkspaceWhenScopeIsNone()
    {
        var root = NewRoot();
        try
        {
            var manager = new ProjectWorkspaceLeaseManager(new(root));
            Assert.Throws<InvalidOperationException>(() => manager.Create(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "work-none",
                AdapterWorkspaceScope.None));
        }
        finally
        {
            DeleteIfPresent(root);
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "aevrix-tests", Guid.NewGuid().ToString("N"));

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void DeleteIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
