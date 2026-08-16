using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class WorkspaceStorageBoundaryTests
{
    [TestMethod]
    public void Constructor_DoesNotExposeRawScopeIdentifiersInPath()
    {
        using var temp = new TemporaryDirectory();
        var scope = new WorkspaceScope("workspace-secret", "user-secret", "key-context-secret");

        var boundary = new WorkspaceStorageBoundary(temp.Path, scope);

        Assert.IsFalse(boundary.RootPath.Contains(scope.WorkspaceId, StringComparison.Ordinal));
        Assert.IsFalse(boundary.RootPath.Contains(scope.UserId, StringComparison.Ordinal));
        Assert.IsFalse(boundary.RootPath.Contains(scope.EncryptionContextId, StringComparison.Ordinal));
        StringAssert.StartsWith(Path.GetFileName(boundary.RootPath), "e-");
    }

    [TestMethod]
    public void ResolvePath_RejectsTraversalAndAbsolutePaths()
    {
        using var temp = new TemporaryDirectory();
        var boundary = CreateBoundary(temp.Path, "workspace-a", "user-a");

        Assert.Throws<UnauthorizedAccessException>(() => boundary.ResolvePath("../outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => boundary.ResolvePath("safe/../../outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => boundary.ResolvePath(Path.GetFullPath(Path.Combine(temp.Path, "outside.txt"))));
    }

    [TestMethod]
    public void DifferentScopes_ResolveToDifferentStorageRoots()
    {
        using var temp = new TemporaryDirectory();
        var first = CreateBoundary(temp.Path, "workspace-a", "user-a");
        var second = CreateBoundary(temp.Path, "workspace-b", "user-a");
        var third = CreateBoundary(temp.Path, "workspace-a", "user-b");

        Assert.AreNotEqual(first.RootPath, second.RootPath);
        Assert.AreNotEqual(first.RootPath, third.RootPath);
    }

    [TestMethod]
    public void OpenWriteAndOpenRead_RoundTripInsideBoundary()
    {
        using var temp = new TemporaryDirectory();
        var boundary = CreateBoundary(temp.Path, "workspace-a", "user-a");
        var payload = new byte[] { 0x41, 0x45, 0x56, 0x52, 0x49, 0x58 };

        using (var output = boundary.OpenWrite("evidence/sample.bin"))
        {
            output.Write(payload);
        }

        using var input = boundary.OpenRead("evidence/sample.bin");
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        CollectionAssert.AreEqual(payload, copy.ToArray());
    }

    [TestMethod]
    public void SameScope_ProducesStableBoundaryNamespace()
    {
        using var temp = new TemporaryDirectory();
        var first = CreateBoundary(temp.Path, "workspace-a", "user-a");
        var second = CreateBoundary(temp.Path, "workspace-a", "user-a");

        Assert.AreEqual(first.RootPath, second.RootPath);
    }

    private static WorkspaceStorageBoundary CreateBoundary(string root, string workspace, string user) =>
        new(root, new WorkspaceScope(workspace, user, "encryption-context-v1"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-workspace-boundary-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
