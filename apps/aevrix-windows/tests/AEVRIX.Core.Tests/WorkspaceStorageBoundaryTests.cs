using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class WorkspaceStorageBoundaryTests
{
    [TestMethod]
    public void ResolveWorkspaceRoot_SeparatesUsersWorkspacesAndEncryptionContexts()
    {
        var boundary = new WorkspaceStorageBoundary(Path.Combine(Path.GetTempPath(), "aevrix-boundary-tests"));
        var baseline = boundary.ResolveWorkspaceRoot(new WorkspaceScope("workspace-a", "user-a", "enc-a"));
        var differentUser = boundary.ResolveWorkspaceRoot(new WorkspaceScope("workspace-a", "user-b", "enc-a"));
        var differentWorkspace = boundary.ResolveWorkspaceRoot(new WorkspaceScope("workspace-b", "user-a", "enc-a"));
        var differentEncryption = boundary.ResolveWorkspaceRoot(new WorkspaceScope("workspace-a", "user-a", "enc-b"));

        Assert.AreNotEqual(baseline, differentUser);
        Assert.AreNotEqual(baseline, differentWorkspace);
        Assert.AreNotEqual(baseline, differentEncryption);
    }

    [TestMethod]
    public void ResolveWorkspaceRoot_DoesNotExposeRawScopeIdentifiers()
    {
        var boundary = new WorkspaceStorageBoundary(Path.Combine(Path.GetTempPath(), "aevrix-boundary-tests"));
        var path = boundary.ResolveWorkspaceRoot(new WorkspaceScope("customer-secret-project", "person@example.com", "tenant-key-42"));

        Assert.IsFalse(path.Contains("customer-secret-project", StringComparison.Ordinal));
        Assert.IsFalse(path.Contains("person@example.com", StringComparison.Ordinal));
        Assert.IsFalse(path.Contains("tenant-key-42", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResolveArtifactPath_RejectsTraversal()
    {
        var boundary = new WorkspaceStorageBoundary(Path.Combine(Path.GetTempPath(), "aevrix-boundary-tests"));
        var scope = new WorkspaceScope("workspace-a", "user-a", "enc-a");

        Assert.ThrowsException<ArgumentException>(() => boundary.ResolveArtifactPath(scope, "evidence", "../other-workspace.bin"));
        Assert.ThrowsException<ArgumentException>(() => boundary.ResolveArtifactPath(scope, "../vault", "item.bin"));
    }

    [TestMethod]
    public void AssertSameScope_RejectsCrossWorkspaceRead()
    {
        var boundary = new WorkspaceStorageBoundary(Path.Combine(Path.GetTempPath(), "aevrix-boundary-tests"));
        var expected = new WorkspaceScope("workspace-a", "user-a", "enc-a");
        var actual = new WorkspaceScope("workspace-b", "user-a", "enc-a");

        Assert.ThrowsException<InvalidOperationException>(() => boundary.AssertSameScope(expected, actual));
    }

    [TestMethod]
    public void AssertSameScope_AllowsExactScope()
    {
        var boundary = new WorkspaceStorageBoundary(Path.Combine(Path.GetTempPath(), "aevrix-boundary-tests"));
        var expected = new WorkspaceScope("workspace-a", "user-a", "enc-a");
        var actual = new WorkspaceScope("workspace-a", "user-a", "enc-a");

        boundary.AssertSameScope(expected, actual);
    }
}
