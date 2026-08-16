using System.Runtime.Versioning;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxRestrictingTokenLeaseTests
{
    private const string SandboxSid = "S-1-5-21-424242-424242-424242-4242";

    [TestMethod]
    public void Create_AddsExplicitRestrictingSidToReducedPrimaryToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows restricting-SID primitive requires Windows.");
            return;
        }

        using var baseToken = WindowsRestrictedTokenLease.Create();
        using var lease = WindowsSandboxRestrictingTokenLease.Create(baseToken, SandboxSid);

        Assert.IsTrue(lease.IsPrimaryToken);
        Assert.IsTrue(lease.RestrictingSidPresent);
        Assert.AreEqual(SandboxSid, lease.SandboxSid);
        Assert.IsFalse(lease.IsClosed);
    }

    [TestMethod]
    public void Create_DoesNotPromoteRestrictingSidToFilesystemIsolationClaim()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows restricting-SID primitive requires Windows.");
            return;
        }

        using var baseToken = WindowsRestrictedTokenLease.Create();
        using var lease = WindowsSandboxRestrictingTokenLease.Create(baseToken, SandboxSid);

        Assert.IsTrue(lease.RestrictingSidPresent);
        // Filesystem isolation remains a separate backend responsibility: an ACL granting this
        // SID to the governed workspace must exist and be tested before that can be attested.
        Assert.IsTrue(lease.IsPrimaryToken);
    }

    [TestMethod]
    public void Create_RejectsMalformedSandboxSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows restricting-SID primitive requires Windows.");
            return;
        }

        using var baseToken = WindowsRestrictedTokenLease.Create();

        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            WindowsSandboxRestrictingTokenLease.Create(baseToken, "not-a-sid"));
    }

    [TestMethod]
    public void Dispose_ClosesSandboxRestrictedTokenHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows restricting-SID primitive requires Windows.");
            return;
        }

        using var baseToken = WindowsRestrictedTokenLease.Create();
        var lease = WindowsSandboxRestrictingTokenLease.Create(baseToken, SandboxSid);
        lease.Dispose();

        Assert.IsTrue(lease.IsClosed);
    }
}
