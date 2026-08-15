using System.Runtime.Versioning;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsRestrictedTokenLeaseTests
{
    [TestMethod]
    public void Create_ProducesPrimaryTokenWithMaximumPrivilegesDisabled()
    {
        using var lease = WindowsRestrictedTokenLease.Create();

        Assert.IsTrue(lease.IsPrimaryToken);
        Assert.IsTrue(lease.MaximumPrivilegesDisabled);
        Assert.IsTrue(lease.EnabledPrivilegeCount <= 1);
        Assert.IsFalse(lease.IsClosed);
    }

    [TestMethod]
    public void Dispose_ClosesRestrictedTokenHandle()
    {
        var lease = WindowsRestrictedTokenLease.Create();
        lease.Dispose();

        Assert.IsTrue(lease.IsClosed);
    }
}
