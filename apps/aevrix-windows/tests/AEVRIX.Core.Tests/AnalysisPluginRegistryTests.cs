using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class AnalysisPluginRegistryTests
{
    private static AnalysisPluginDescriptor Plugin(
        string id,
        IReadOnlyCollection<string>? domains = null,
        IReadOnlyCollection<string>? systems = null,
        IReadOnlyCollection<string>? languages = null,
        IReadOnlyCollection<string>? formats = null,
        IReadOnlyCollection<AnalysisTechnique>? techniques = null,
        bool mayProcessPersonalData = false) => new(
            PluginId: id,
            Version: "1.0.0",
            Domains: domains ?? ["*"],
            Systems: systems ?? ["*"],
            Languages: languages ?? ["*"],
            Formats: formats ?? ["*"],
            Techniques: techniques ?? [AnalysisTechnique.StaticInspection],
            RequiresNetwork: false,
            MayProcessPersonalData: mayProcessPersonalData);

    private static AnalysisExecutionRequest Request(
        TargetAccessClass accessClass = TargetAccessClass.Owned,
        AnalysisTechnique technique = AnalysisTechnique.StaticInspection,
        string domain = "software",
        string system = "linux",
        IReadOnlyCollection<string>? languages = null,
        IReadOnlyCollection<string>? formats = null,
        AnalysisEvidenceSensitivity sensitivity = AnalysisEvidenceSensitivity.Internal,
        OutputBoundary outputBoundary = OutputBoundary.LocalWorkspaceOnly,
        bool authBypass = false,
        bool crossWorkspace = false) => new(
            RequestId: "req-router-001",
            Scope: new WorkspaceScope("workspace-router-a", "user-router-a", "key-router-a"),
            Target: new AnalysisTarget(
                "target-router-001",
                accessClass,
                domain,
                system,
                languages ?? ["csharp"],
                formats ?? ["source"]),
            Technique: technique,
            Sensitivity: sensitivity,
            OutputBoundary: outputBoundary,
            AuthenticationOrAccessControlBypassRequested: authBypass,
            LicenseOrDrmBypassRequested: false,
            CrossWorkspaceReadRequested: crossWorkspace);

    [TestMethod]
    public void ExactMultiDomainAdapterWinsOverGenericAdapter()
    {
        var generic = Plugin("generic");
        var geospatial = Plugin(
            "geospatial-rust",
            domains: ["geospatial"],
            systems: ["linux"],
            languages: ["rust", "python"],
            formats: ["geojson", "parquet"]);
        var registry = new AnalysisPluginRegistry([generic, geospatial]);

        var selection = registry.Resolve(Request(
            domain: "geospatial",
            system: "linux",
            languages: ["rust", "python"],
            formats: ["geojson", "parquet"]));

        Assert.AreEqual("geospatial-rust", selection.Plugin.PluginId);
        Assert.IsTrue(selection.SpecificityScore > 0);
    }

    [TestMethod]
    public void ExplicitPriorityCanSelectGovernedAdapter()
    {
        var first = Plugin("adapter-a", domains: ["software"]);
        var second = Plugin("adapter-b", domains: ["software"]);
        var registry = new AnalysisPluginRegistry([(first, 10), (second, 20)]);

        var selection = registry.Resolve(Request());

        Assert.AreEqual("adapter-b", selection.Plugin.PluginId);
        Assert.AreEqual(20, selection.Priority);
    }

    [TestMethod]
    public void EqualRankedAdaptersFailClosedInsteadOfRoutingArbitrarily()
    {
        var registry = new AnalysisPluginRegistry([
            Plugin("adapter-a", domains: ["software"]),
            Plugin("adapter-b", domains: ["software"])
        ]);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve(Request()));
        StringAssert.Contains(ex.Message, "ambiguous");
    }

    [TestMethod]
    public void UnsupportedLanguageOrFormatFailsClosed()
    {
        var registry = new AnalysisPluginRegistry([
            Plugin(
                "python-json",
                domains: ["software"],
                systems: ["linux"],
                languages: ["python"],
                formats: ["json"])
        ]);

        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve(Request(
            languages: ["rust"],
            formats: ["wasm"])));
    }

    [TestMethod]
    public void ThirdPartyCleanRoomCannotRouteToDeepInstrumentation()
    {
        var registry = new AnalysisPluginRegistry([
            Plugin(
                "deep-runtime",
                techniques: [AnalysisTechnique.AuthorizedDynamicInstrumentation])
        ]);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve(Request(
            accessClass: TargetAccessClass.ThirdPartyCleanRoom,
            technique: AnalysisTechnique.AuthorizedDynamicInstrumentation)));

        StringAssert.Contains(ex.Message, "execution policy");
    }

    [TestMethod]
    public void BypassAndCrossWorkspaceRequestsNeverReachAPlugin()
    {
        var registry = new AnalysisPluginRegistry([Plugin("generic")]);

        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve(Request(authBypass: true)));
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve(Request(crossWorkspace: true)));
    }

    [TestMethod]
    public void PersonalDataRequiresDeclaredCapabilityAndLocalBoundary()
    {
        var noPersonalData = new AnalysisPluginRegistry([Plugin("privacy-minimized")]);
        Assert.ThrowsExactly<InvalidOperationException>(() => noPersonalData.Resolve(Request(
            sensitivity: AnalysisEvidenceSensitivity.PersonalData)));

        var allowed = new AnalysisPluginRegistry([Plugin("privacy-aware", mayProcessPersonalData: true)]);
        var selection = allowed.Resolve(Request(
            sensitivity: AnalysisEvidenceSensitivity.PersonalData,
            outputBoundary: OutputBoundary.LocalWorkspaceOnly));
        Assert.AreEqual("privacy-aware", selection.Plugin.PluginId);

        Assert.ThrowsExactly<InvalidOperationException>(() => allowed.Resolve(Request(
            sensitivity: AnalysisEvidenceSensitivity.PersonalData,
            outputBoundary: OutputBoundary.RedactedExternal)));
    }

    [TestMethod]
    public void DuplicatePluginIdentityIsRejectedAtRegistration()
    {
        var first = Plugin("duplicate");
        var second = Plugin("duplicate");

        Assert.ThrowsExactly<ArgumentException>(() => new AnalysisPluginRegistry([first, second]));
    }
}
