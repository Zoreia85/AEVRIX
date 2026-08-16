using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class AnalysisPluginContractTests
{
    private static readonly AnalysisPluginDescriptor GeneralPlugin = new(
        PluginId: "generic-source-analyzer",
        Version: "1.0.0",
        Domains: ["*"],
        Systems: ["*"],
        Languages: ["*"],
        Formats: ["*"],
        Techniques:
        [
            AnalysisTechnique.StaticInspection,
            AnalysisTechnique.AuthorizedRuntimeObservation,
            AnalysisTechnique.AuthorizedDynamicInstrumentation
        ],
        RequiresNetwork: false,
        MayProcessPersonalData: true);

    private static AnalysisExecutionRequest Request(
        TargetAccessClass accessClass,
        AnalysisTechnique technique,
        AnalysisEvidenceSensitivity sensitivity = AnalysisEvidenceSensitivity.Internal,
        OutputBoundary outputBoundary = OutputBoundary.LocalWorkspaceOnly,
        bool authBypass = false,
        bool drmBypass = false,
        bool crossWorkspace = false) => new(
            RequestId: "req-001",
            Scope: new WorkspaceScope("workspace-a", "user-a", "key-context-a"),
            Target: new AnalysisTarget(
                "target-001",
                accessClass,
                Domain: "software",
                System: "desktop",
                Languages: ["csharp"],
                Formats: ["pe"]),
            Technique: technique,
            Sensitivity: sensitivity,
            OutputBoundary: outputBoundary,
            AuthenticationOrAccessControlBypassRequested: authBypass,
            LicenseOrDrmBypassRequested: drmBypass,
            CrossWorkspaceReadRequested: crossWorkspace);

    [TestMethod]
    public void GenericPluginSupportsMultipleDomainsLanguagesFormatsAndSystems()
    {
        var target = new AnalysisTarget(
            "target-any",
            TargetAccessClass.Owned,
            Domain: "geospatial",
            System: "linux",
            Languages: ["rust", "python"],
            Formats: ["geojson", "parquet"]);

        Assert.IsTrue(GeneralPlugin.Supports(target, AnalysisTechnique.StaticInspection));
    }

    [TestMethod]
    public void OwnedTargetAllowsDeepAuthorizedInstrumentation()
    {
        Request(TargetAccessClass.Owned, AnalysisTechnique.AuthorizedDynamicInstrumentation)
            .ValidateAgainst(GeneralPlugin);
    }

    [TestMethod]
    public void ExplicitlyAuthorizedTargetAllowsDeepAuthorizedInstrumentation()
    {
        Request(TargetAccessClass.ExplicitlyAuthorized, AnalysisTechnique.AuthorizedDynamicInstrumentation)
            .ValidateAgainst(GeneralPlugin);
    }

    [TestMethod]
    public void ThirdPartyCleanRoomRejectsDeepInstrumentation()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            Request(TargetAccessClass.ThirdPartyCleanRoom, AnalysisTechnique.AuthorizedDynamicInstrumentation)
                .ValidateAgainst(GeneralPlugin));

        StringAssert.Contains(ex.Message, "clean-room");
    }

    [TestMethod]
    public void ThirdPartyCleanRoomAllowsStaticInspection()
    {
        Request(TargetAccessClass.ThirdPartyCleanRoom, AnalysisTechnique.StaticInspection)
            .ValidateAgainst(GeneralPlugin);
    }

    [TestMethod]
    public void BypassRequestsAreAlwaysRejected()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Request(TargetAccessClass.Owned, AnalysisTechnique.StaticInspection, authBypass: true)
                .ValidateAgainst(GeneralPlugin));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Request(TargetAccessClass.Owned, AnalysisTechnique.StaticInspection, drmBypass: true)
                .ValidateAgainst(GeneralPlugin));
    }

    [TestMethod]
    public void CrossWorkspaceReadIsRejected()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Request(TargetAccessClass.Owned, AnalysisTechnique.StaticInspection, crossWorkspace: true)
                .ValidateAgainst(GeneralPlugin));
    }

    [TestMethod]
    public void PersonalDataCannotLeaveWorkspaceBoundary()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Request(
                TargetAccessClass.Owned,
                AnalysisTechnique.StaticInspection,
                AnalysisEvidenceSensitivity.PersonalData,
                OutputBoundary.RedactedExternal)
                .ValidateAgainst(GeneralPlugin));
    }

    [TestMethod]
    public void EvidenceBlueprintBindingIsWorkspaceBoundAndDeterministic()
    {
        var evidence = new string('a', 64);
        var blueprint = new string('b', 64);

        var first = new EvidenceBlueprintBinding(
            "workspace-a", evidence, blueprint, "plugin-a", "req-a").ComputeBindingSha256();
        var same = new EvidenceBlueprintBinding(
            "workspace-a", evidence, blueprint, "plugin-a", "req-a").ComputeBindingSha256();
        var otherWorkspace = new EvidenceBlueprintBinding(
            "workspace-b", evidence, blueprint, "plugin-a", "req-a").ComputeBindingSha256();

        Assert.AreEqual(first, same);
        Assert.AreEqual(64, first.Length);
        Assert.AreNotEqual(first, otherWorkspace);
    }
}
