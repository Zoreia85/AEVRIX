using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class CapabilityPluginContractTests
{
    private static readonly CapabilitySource Source = new(
        "open-source/tooling",
        "Apache-2.0",
        "1111111111111111111111111111111111111111",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    [TestMethod]
    public void ApprovedDomainNeutralPlugin_AcceptsMinimizedStaticAnalysis()
    {
        var contract = CreateContract();
        var context = new CapabilityExecutionContext(
            "workspace-alpha",
            "subject-alpha",
            TargetAuthorizationScope.ThirdPartyCleanRoom,
            AnalysisTechnique.Static,
            OutboundNetworkRequested: false,
            SecretMaterialRequested: false,
            RequestedDataExposure: DataExposureClass.MetadataOnly);

        var decision = CapabilityPluginAdmissionPolicy.Evaluate(contract, context);

        Assert.IsTrue(decision.Allowed, decision.Reason);
        CollectionAssert.Contains(contract.Domains.ToArray(), "source-code");
        CollectionAssert.Contains(contract.Languages.ToArray(), "CSharp");
        CollectionAssert.Contains(contract.Formats.ToArray(), "json");
        CollectionAssert.Contains(contract.OperatingSystems.ToArray(), "windows");
    }

    [TestMethod]
    public void ThirdPartyCleanRoom_RejectsRuntimeInstrumentation()
    {
        var contract = CreateContract() with
        {
            Techniques = new[] { AnalysisTechnique.Static, AnalysisTechnique.RuntimeInstrumentation }
        };
        var context = new CapabilityExecutionContext(
            "workspace-alpha",
            "subject-alpha",
            TargetAuthorizationScope.ThirdPartyCleanRoom,
            AnalysisTechnique.RuntimeInstrumentation,
            OutboundNetworkRequested: false,
            SecretMaterialRequested: false,
            RequestedDataExposure: DataExposureClass.MetadataOnly);

        var decision = CapabilityPluginAdmissionPolicy.Evaluate(contract, context);

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason, "owned or explicitly authorized");
    }

    [TestMethod]
    [DataRow(TargetAuthorizationScope.OwnedSystem)]
    [DataRow(TargetAuthorizationScope.ExplicitlyAuthorizedSystem)]
    public void AuthorizedTargets_MayUseDeclaredRuntimeInstrumentation(TargetAuthorizationScope scope)
    {
        var contract = CreateContract() with
        {
            Techniques = new[] { AnalysisTechnique.Static, AnalysisTechnique.RuntimeInstrumentation }
        };
        var context = new CapabilityExecutionContext(
            "workspace-alpha",
            "subject-alpha",
            scope,
            AnalysisTechnique.RuntimeInstrumentation,
            OutboundNetworkRequested: false,
            SecretMaterialRequested: false,
            RequestedDataExposure: DataExposureClass.MetadataOnly);

        var decision = CapabilityPluginAdmissionPolicy.Evaluate(contract, context);

        Assert.IsTrue(decision.Allowed, decision.Reason);
    }

    [TestMethod]
    public void Plugin_MustBeWorkspaceAndSubjectBound()
    {
        var contract = CreateContract() with { RequiresSubjectBinding = false };

        Assert.IsFalse(contract.CanRegister());
        Assert.ThrowsExactly<InvalidOperationException>(() => contract.Validate());
    }

    [TestMethod]
    public void Admission_RejectsUndeclaredNetworkSecretAndExcessDataExposure()
    {
        var contract = CreateContract();

        var network = CapabilityPluginAdmissionPolicy.Evaluate(contract, Context(network: true));
        var secret = CapabilityPluginAdmissionPolicy.Evaluate(contract, Context(secret: true));
        var exposure = CapabilityPluginAdmissionPolicy.Evaluate(
            contract,
            Context(exposure: DataExposureClass.MinimizedContent));

        Assert.IsFalse(network.Allowed);
        Assert.IsFalse(secret.Allowed);
        Assert.IsFalse(exposure.Allowed);
    }

    [TestMethod]
    public void DeniedCapability_CannotRegisterAsPlugin()
    {
        var contract = CreateContract() with { Capability = "captcha-bypass" };

        Assert.IsFalse(contract.CanRegister());
    }

    private static CapabilityPluginContract CreateContract() => new(
        "generic-analysis-adapter",
        "evidence-analysis",
        Source,
        CapabilityApprovalState.Approved,
        Domains: new[] { "source-code", "document", "binary-metadata" },
        Languages: new[] { "CSharp", "Python", "Go", "JavaScript" },
        Formats: new[] { "json", "xml", "text", "pe" },
        OperatingSystems: new[] { "windows", "linux", "macos" },
        Techniques: new[] { AnalysisTechnique.Static, AnalysisTechnique.Dynamic },
        RequiresWorkspaceBinding: true,
        RequiresSubjectBinding: true,
        AllowsOutboundNetwork: false,
        AllowsSecretMaterial: false,
        MaximumDataExposure: DataExposureClass.MetadataOnly);

    private static CapabilityExecutionContext Context(
        bool network = false,
        bool secret = false,
        DataExposureClass exposure = DataExposureClass.MetadataOnly) => new(
        "workspace-alpha",
        "subject-alpha",
        TargetAuthorizationScope.OwnedSystem,
        AnalysisTechnique.Static,
        network,
        secret,
        exposure);
}
