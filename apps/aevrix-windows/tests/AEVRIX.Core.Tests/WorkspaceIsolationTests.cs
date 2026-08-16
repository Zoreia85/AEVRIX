using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class WorkspaceIsolationTests
{
    [TestMethod]
    public void Paths_DoNotExposeRawUserOrWorkspaceIdentifiers()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-isolation-tests");
        var isolation = new WorkspaceIsolation(root);

        var path = isolation.EvidenceRoot("synthetic-user-alpha", "Synthetic Workspace Alpha / Case 42");

        Assert.IsFalse(path.Contains("synthetic-user-alpha", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(path.Contains("Synthetic Workspace Alpha", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(path, Path.Combine("workspaces", WorkspaceIsolation.OpaqueId("wsp", "Synthetic Workspace Alpha / Case 42")));
    }

    [TestMethod]
    public void DifferentUsers_CannotCollideOnSameWorkspaceIdentifier()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-isolation-tests");
        var isolation = new WorkspaceIsolation(root);

        var first = isolation.WorkspaceRoot("user-a", "shared-name");
        var second = isolation.WorkspaceRoot("user-b", "shared-name");

        Assert.AreNotEqual(first, second);
        Assert.IsTrue(first.StartsWith(isolation.UserRoot("user-a"), PathComparison()));
        Assert.IsTrue(second.StartsWith(isolation.UserRoot("user-b"), PathComparison()));
    }

    [TestMethod]
    public void ResolveWorkspaceFile_RejectsTraversalOutsideBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-isolation-tests");
        var isolation = new WorkspaceIsolation(root);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            isolation.ResolveWorkspaceFile("user-a", "workspace-a", Path.Combine("..", "..", "escape.txt")));
    }

    [TestMethod]
    public void ResolveWorkspaceFile_RejectsAbsolutePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-isolation-tests");
        var isolation = new WorkspaceIsolation(root);
        var absolute = Path.GetFullPath(Path.Combine(root, "escape.txt"));

        Assert.ThrowsExactly<ArgumentException>(() =>
            isolation.ResolveWorkspaceFile("user-a", "workspace-a", absolute));
    }

    [TestMethod]
    public void OpaqueIds_AreStablePurposeSeparatedAndFixedLength()
    {
        var first = WorkspaceIsolation.OpaqueId("usr", "same-value");
        var again = WorkspaceIsolation.OpaqueId("usr", "same-value");
        var otherPurpose = WorkspaceIsolation.OpaqueId("wsp", "same-value");

        Assert.AreEqual(first, again);
        Assert.AreNotEqual(first, otherPurpose);
        Assert.AreEqual(32, first.Length);
        Assert.IsTrue(first.All(ch => char.IsAsciiHexDigit(ch) && !char.IsUpper(ch)));
    }

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
