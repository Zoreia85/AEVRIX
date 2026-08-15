using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsRestrictedTokenLeaseTests
{
    [TestMethod]
    public void Create_ProducesPrimaryTokenWithMaximumPrivilegesDisabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows restricted-token primitive requires Windows.");
        }

        using var lease = WindowsRestrictedTokenLease.Create();

        Assert.IsTrue(lease.IsPrimaryToken);
        Assert.IsTrue(lease.MaximumPrivilegesDisabled);
        Assert.IsTrue(lease.EnabledPrivilegeCount <= 1);
        Assert.IsFalse(lease.IsClosed);
    }

    [TestMethod]
    public void Dispose_ClosesRestrictedTokenHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows restricted-token primitive requires Windows.");
        }

        var lease = WindowsRestrictedTokenLease.Create();
        lease.Dispose();

        Assert.IsTrue(lease.IsClosed);
    }
}
