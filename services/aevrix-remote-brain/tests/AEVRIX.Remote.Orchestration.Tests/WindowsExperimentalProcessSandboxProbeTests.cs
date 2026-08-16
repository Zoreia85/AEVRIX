using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsExperimentalProcessSandboxProbeTests
{
    [TestMethod]
    public void GovernancePolicy_DefaultsToDisabledEvenWhenCapabilityIsAvailable()
    {
        var capability = AvailableCapability();
        var policy = new ExperimentalProcessSandboxGovernancePolicy();

        Assert.IsFalse(policy.AllowsUse(capability));
    }

    [TestMethod]
    public void GovernancePolicy_AllowsOnlyExplicitlyEnabledReviewedContract()
    {
        var policy = new ExperimentalProcessSandboxGovernancePolicy(Enabled: true);

        Assert.IsTrue(policy.AllowsUse(AvailableCapability()));
        Assert.IsFalse(policy.AllowsUse(AvailableCapability() with
        {
            CreateProcessAsUserInSandboxAvailable = false
        }));
        Assert.IsFalse(policy.AllowsUse(AvailableCapability() with
        {
            Experimental = false
        }));
    }

    [TestMethod]
    public void GovernancePolicy_RejectsUnreviewedContractVersion()
    {
        var policy = new ExperimentalProcessSandboxGovernancePolicy(
            Enabled: true,
            ApprovedContractVersion: "0.2.0");

        Assert.Throws<ArgumentException>(policy.Validate);
    }

    [TestMethod]
    public void Probe_UsesOnlyWindowsSystemDirectoryAndReturnsCoherentFeatureState()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Experimental Windows sandbox feature detection requires Windows.");
            return;
        }

        var capability = new WindowsExperimentalProcessSandboxProbe().Probe();

        Assert.AreEqual(WindowsExperimentalProcessSandboxProbe.KnownContractVersion, capability.ContractVersion);
        Assert.IsTrue(capability.Experimental);
        if (!capability.ModulePresent)
        {
            Assert.IsFalse(capability.CreateProcessInSandboxAvailable);
            Assert.IsFalse(capability.CreateProcessAsUserInSandboxAvailable);
            Assert.IsNull(capability.ModulePath);
            return;
        }

        Assert.IsNotNull(capability.ModulePath);
        var expected = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "processmodel.dll"));
        Assert.AreEqual(expected, Path.GetFullPath(capability.ModulePath), ignoreCase: true);
        Assert.IsTrue(File.Exists(capability.ModulePath));
    }

    private static WindowsExperimentalProcessSandboxCapability AvailableCapability() => new(
        ModulePresent: true,
        CreateProcessInSandboxAvailable: true,
        CreateProcessAsUserInSandboxAvailable: true,
        ContractVersion: WindowsExperimentalProcessSandboxProbe.KnownContractVersion,
        Experimental: true,
        ModulePath: @"C:\Windows\System32\processmodel.dll");
}
