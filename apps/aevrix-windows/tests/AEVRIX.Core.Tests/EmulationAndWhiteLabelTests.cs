using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class EmulationAndWhiteLabelTests
{
    [TestMethod]
    public void DefaultEmulationPlan_IsSandboxedAndOffline()
    {
        var plan = EmulationPlan.CreateDefault(
            Guid.NewGuid(),
            InvestigationTargetKind.DesktopApplication,
            [new InvestigationInputArtifact("setup.exe", "C:\\input\\setup.exe")]);

        Assert.AreEqual(EmulationIsolationLevel.DisposableSandbox, plan.IsolationLevel);
        Assert.AreEqual(EmulationNetworkPolicy.Disabled, plan.NetworkPolicy);
        Assert.IsFalse(plan.ElevationExplicitlyApproved);
        Assert.IsFalse(plan.DestructiveHostChangesApproved);
        Assert.IsTrue(plan.Steps.Any(step => step.Kind == EmulationTestKind.Uninstall));
    }

    [TestMethod]
    public void Emulation_RejectsDestructiveHostChangesWithoutDisposableIsolation()
    {
        var plan = new EmulationPlan(
            Guid.NewGuid(),
            InvestigationTargetKind.DesktopApplication,
            EmulationIsolationLevel.ProcessRestricted,
            EmulationNetworkPolicy.Disabled,
            Array.Empty<string>(),
            [new InvestigationInputArtifact("setup.exe", "C:\\input\\setup.exe")],
            [new EmulationTestStep("install", EmulationTestKind.Install, TimeSpan.FromMinutes(10), Array.Empty<string>())],
            ElevationExplicitlyApproved: true,
            DestructiveHostChangesApproved: true);

        Assert.Throws<InvalidOperationException>(plan.Validate);
    }

    [TestMethod]
    public void WhiteLabelSpecification_RejectsOriginalBrandAssets()
    {
        var spec = CreateValidWhiteLabelSpec() with { OriginalTrademarkAssetsIncluded = true };
        Assert.Throws<InvalidOperationException>(spec.Validate);
    }

    [TestMethod]
    public void WhiteLabelSpecification_HashIsDeterministic()
    {
        var spec = CreateValidWhiteLabelSpec();
        var first = spec.ComputeSpecificationSha256();
        var second = spec.ComputeSpecificationSha256();

        Assert.AreEqual(64, first.Length);
        Assert.AreEqual(first, second);
    }

    private static WhiteLabelBuildSpecification CreateValidWhiteLabelSpec()
        => new(
            "workspace-001",
            new string('a', 64),
            new WhiteLabelBranding(
                "Produto Novo",
                "Publisher Novo",
                null,
                "#FFFFFF",
                "#101820",
                "#0088CC"),
            [
                new WhiteLabelRequirementBinding(
                    "REQ-001",
                    ["EV-001"],
                    BehaviorRequired: true,
                    OriginalExpressionForbidden: true)
            ],
            RestrictedSourceCodeAccessed: false,
            OriginalTrademarkAssetsIncluded: false,
            OriginalSecretsIncluded: false);
}
