using System.Runtime.Versioning;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
[SupportedOSPlatform("windows8.0")]
public sealed class WindowsAppContainerProfileLeaseTests
{
    [TestMethod]
    public void CreateEphemeral_ProducesOpaqueAppContainerIdentity()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
        {
            Assert.Inconclusive("Windows AppContainer profiles require Windows 8 / Windows Server 2012 or newer.");
            return;
        }

        var projectId = Guid.NewGuid();
        using var lease = WindowsAppContainerProfileLease.CreateEphemeral(projectId, "static-analysis");

        Assert.IsTrue(lease.ProfileCreated);
        Assert.IsTrue(lease.DeleteOnDispose);
        Assert.IsTrue(lease.SidString.StartsWith("S-1-15-2-", StringComparison.Ordinal));
        Assert.IsFalse(lease.ProfileName.Contains(projectId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(lease.ProfileName.Contains("static-analysis", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(lease.ProfileName.StartsWith("AEVRIX.", StringComparison.Ordinal));
        Assert.IsFalse(lease.IsDisposed);
    }

    [TestMethod]
    public void Create_ExistingProfileDoesNotTakeDeletionOwnership()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
        {
            Assert.Inconclusive("Windows AppContainer profiles require Windows 8 / Windows Server 2012 or newer.");
            return;
        }

        var name = "AEVRIX.test." + Guid.NewGuid().ToString("N")[..20];
        var owner = WindowsAppContainerProfileLease.Create(name);
        try
        {
            using (var observer = WindowsAppContainerProfileLease.Create(name))
            {
                Assert.IsFalse(observer.ProfileCreated);
                Assert.IsFalse(observer.DeleteOnDispose);
                Assert.AreEqual(owner.SidString, observer.SidString);
            }

            using var stillExisting = WindowsAppContainerProfileLease.Create(name);
            Assert.IsFalse(stillExisting.ProfileCreated);
        }
        finally
        {
            owner.Dispose();
        }

        using var recreated = WindowsAppContainerProfileLease.Create(name);
        Assert.IsTrue(recreated.ProfileCreated);
    }

    [TestMethod]
    public void Dispose_IsIdempotentAndReleasesIdentity()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
        {
            Assert.Inconclusive("Windows AppContainer profiles require Windows 8 / Windows Server 2012 or newer.");
            return;
        }

        var lease = WindowsAppContainerProfileLease.CreateEphemeral(Guid.NewGuid(), "vision-ocr");
        lease.Dispose();
        lease.Dispose();

        Assert.IsTrue(lease.IsDisposed);
    }
}
