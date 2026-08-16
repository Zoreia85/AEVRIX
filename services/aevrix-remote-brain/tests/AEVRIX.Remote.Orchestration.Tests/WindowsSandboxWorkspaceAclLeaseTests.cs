using System.Runtime.Versioning;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxWorkspaceAclLeaseTests
{
    private const string SandboxSid = "S-1-5-21-424242-424242-424242-4242";

    [TestMethod]
    public void Create_AppliesAndVerifiesInheritableSandboxGrant()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows workspace ACL primitive requires Windows.");
            return;
        }

        var root = CreateTemporaryDirectory();
        try
        {
            using var lease = WindowsSandboxWorkspaceAclLease.Create(
                root,
                SandboxSid,
                SandboxWorkspaceAccess.ReadWrite);

            Assert.IsTrue(lease.AclGrantVerified);
            Assert.AreEqual(SandboxSid, lease.SandboxSid);
            Assert.AreEqual(SandboxWorkspaceAccess.ReadWrite, lease.Access);
            Assert.IsFalse(lease.IsDisposed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Create_ReadOnlyGrantDoesNotClaimReadWriteAuthority()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows workspace ACL primitive requires Windows.");
            return;
        }

        var root = CreateTemporaryDirectory();
        try
        {
            using var lease = WindowsSandboxWorkspaceAclLease.Create(
                root,
                SandboxSid,
                SandboxWorkspaceAccess.ReadOnly);

            Assert.IsTrue(lease.AclGrantVerified);
            Assert.AreEqual(SandboxWorkspaceAccess.ReadOnly, lease.Access);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Dispose_RestoresOriginalDaclAndAllowsLeaseToBeAppliedAgain()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows workspace ACL primitive requires Windows.");
            return;
        }

        var root = CreateTemporaryDirectory();
        try
        {
            var first = WindowsSandboxWorkspaceAclLease.Create(
                root,
                SandboxSid,
                SandboxWorkspaceAccess.ReadWrite);
            first.Dispose();
            Assert.IsTrue(first.IsDisposed);

            using var second = WindowsSandboxWorkspaceAclLease.Create(
                root,
                SandboxSid,
                SandboxWorkspaceAccess.ReadOnly);
            Assert.IsTrue(second.AclGrantVerified);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Create_RejectsMalformedSidAndMissingWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows workspace ACL primitive requires Windows.");
            return;
        }

        var root = CreateTemporaryDirectory();
        try
        {
            Assert.Throws<System.ComponentModel.Win32Exception>(() =>
                WindowsSandboxWorkspaceAclLease.Create(root, "not-a-sid", SandboxWorkspaceAccess.ReadOnly));

            var missing = Path.Combine(root, "missing");
            Assert.Throws<DirectoryNotFoundException>(() =>
                WindowsSandboxWorkspaceAclLease.Create(missing, SandboxSid, SandboxWorkspaceAccess.ReadOnly));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
