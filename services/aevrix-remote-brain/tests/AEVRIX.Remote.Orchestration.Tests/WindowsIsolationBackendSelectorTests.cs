using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsIsolationBackendSelectorTests
{
    [TestMethod]
    public void RestrictedFilesystem_IsDeniedWhenExperimentalSurfaceIsUnavailable()
    {
        RequireWindows();
        var selector = new WindowsIsolationBackendSelector(new ExperimentalProcessSandboxGovernancePolicy(Enabled: true));

        var decision = selector.Select(
            Authority(OutOfProcessNetworkScope.None, OutOfProcessFilesystemScope.WorkspaceOnly),
            UnavailableExperimentalCapability());

        Assert.IsFalse(decision.PolicyEligible);
        Assert.AreEqual(WindowsIsolationBackendKind.None, decision.Backend);
        Assert.AreEqual("FilesystemIsolationBackendUnavailable", decision.DecisionCode);
    }

    [TestMethod]
    public void AvailableExperimentalSurface_RemainsDeniedWithoutGovernanceOptIn()
    {
        RequireWindows();
        var selector = new WindowsIsolationBackendSelector(new ExperimentalProcessSandboxGovernancePolicy());

        var decision = selector.Select(
            Authority(OutOfProcessNetworkScope.None, OutOfProcessFilesystemScope.WorkspaceOnly),
            AvailableExperimentalCapability());

        Assert.IsFalse(decision.PolicyEligible);
        Assert.AreEqual("ExperimentalFilesystemIsolationNotGovernanceAuthorized", decision.DecisionCode);
    }

    [TestMethod]
    public void ExperimentalCandidate_RequiresAvailabilityAndGovernance()
    {
        RequireWindows();
        var selector = new WindowsIsolationBackendSelector(new ExperimentalProcessSandboxGovernancePolicy(Enabled: true));

        var decision = selector.Select(
            Authority(OutOfProcessNetworkScope.None, OutOfProcessFilesystemScope.WorkspaceOnly),
            AvailableExperimentalCapability());

        Assert.IsTrue(decision.PolicyEligible);
        Assert.AreEqual(WindowsIsolationBackendKind.ExperimentalProcessSandboxCandidate, decision.Backend);
        Assert.AreEqual("EligibleExperimentalProcessSandboxCandidate", decision.DecisionCode);
    }

    [TestMethod]
    public void ExistingAppContainerCandidate_IsLimitedToNoNetworkAndUnrestrictedFilesystem()
    {
        RequireWindows();
        var selector = new WindowsIsolationBackendSelector(new ExperimentalProcessSandboxGovernancePolicy());

        var supported = selector.Select(
            Authority(OutOfProcessNetworkScope.None, OutOfProcessFilesystemScope.Unrestricted),
            UnavailableExperimentalCapability());
        var unsupported = selector.Select(
            Authority(OutOfProcessNetworkScope.LoopbackOnly, OutOfProcessFilesystemScope.Unrestricted),
            UnavailableExperimentalCapability());

        Assert.IsTrue(supported.PolicyEligible);
        Assert.AreEqual(WindowsIsolationBackendKind.ZeroCapabilityAppContainer, supported.Backend);
        Assert.IsFalse(unsupported.PolicyEligible);
        Assert.AreEqual("NetworkIsolationBackendUnavailable", unsupported.DecisionCode);
    }

    [TestMethod]
    public void UnrestrictedAuthority_SelectsLocalPolicyCandidate()
    {
        RequireWindows();
        var selector = new WindowsIsolationBackendSelector(new ExperimentalProcessSandboxGovernancePolicy());

        var decision = selector.Select(
            Authority(OutOfProcessNetworkScope.Unrestricted, OutOfProcessFilesystemScope.Unrestricted),
            UnavailableExperimentalCapability());

        Assert.IsTrue(decision.PolicyEligible);
        Assert.AreEqual(WindowsIsolationBackendKind.LocalUnrestricted, decision.Backend);
    }

    [TestMethod]
    public void Selection_IsNotLaunchAuthority()
    {
        var type = typeof(WindowsIsolationBackendSelection);
        Assert.IsNull(type.GetProperty("LaunchAuthorized"));
        Assert.IsNull(type.GetProperty("LaunchEligible"));
        Assert.IsNotNull(type.GetProperty("PolicyEligible"));
    }

    private static OutOfProcessAuthorityPolicy Authority(
        OutOfProcessNetworkScope network,
        OutOfProcessFilesystemScope filesystem) => new(
            new OutOfProcessNetworkPolicy(network),
            new OutOfProcessFilesystemPolicy(filesystem));

    private static WindowsExperimentalProcessSandboxCapability AvailableExperimentalCapability() => new(
        ModulePresent: true,
        CreateProcessInSandboxAvailable: true,
        CreateProcessAsUserInSandboxAvailable: true,
        ContractVersion: WindowsExperimentalProcessSandboxProbe.KnownContractVersion,
        Experimental: true,
        ModulePath: @"C:\Windows\System32\processmodel.dll");

    private static WindowsExperimentalProcessSandboxCapability UnavailableExperimentalCapability() => new(
        ModulePresent: false,
        CreateProcessInSandboxAvailable: false,
        CreateProcessAsUserInSandboxAvailable: false,
        ContractVersion: WindowsExperimentalProcessSandboxProbe.KnownContractVersion,
        Experimental: true,
        ModulePath: null);

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows isolation backend selector tests require Windows.");
        }
    }
}
