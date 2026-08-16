using System.Runtime.Versioning;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsAppContainerProfileLeaseTests
{
    [TestMethod]
    public void Create_ProducesOpaqueVerifiedAppContainerIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows AppContainer profile primitive requires Windows.");
            return;
        }

        using var lease = WindowsAppContainerProfileLease.Create();

        Assert.IsTrue(lease.ProfileCreated);
        Assert.IsTrue(lease.ProfileName.StartsWith("AEVRIX.Sandbox.", StringComparison.Ordinal));
        Assert.IsTrue(lease.ProfileName.Length <= 64);
        Assert.IsTrue(lease.AppContainerSid.StartsWith("S-1-15-2-", StringComparison.Ordinal));
        Assert.IsFalse(lease.ProfileName.Contains("project", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(lease.ProfileName.Contains("user", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Dispose_DeletesProfileAndAllowsFreshProfileLifecycle()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows AppContainer profile primitive requires Windows.");
            return;
        }

        var first = WindowsAppContainerProfileLease.Create();
        var firstName = first.ProfileName;
        first.Dispose();

        Assert.IsTrue(first.IsDisposed);
        Assert.IsFalse(first.ProfileCreated);

        using var second = WindowsAppContainerProfileLease.Create();
        Assert.IsTrue(second.ProfileCreated);
        Assert.AreNotEqual(firstName, second.ProfileName);
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows AppContainer profile primitive requires Windows.");
            return;
        }

        var lease = WindowsAppContainerProfileLease.Create();
        lease.Dispose();
        lease.Dispose();

        Assert.IsTrue(lease.IsDisposed);
    }
}
