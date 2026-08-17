using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ArtifactInspectionTests
{
    [TestMethod]
    public void ArchivePlanIsReadOnlyOfflineAndNonExecuting()
    {
        var route = TargetIntakeRouter.ClassifyArtifact(Path.Combine(Path.GetTempPath(), "sample.zip"));
        var plan = ArtifactInspectionPlanner.Create(route, encrypted: false);

        Assert.IsTrue(plan.Policy.ReadOnly);
        Assert.IsFalse(plan.Policy.NetworkAllowed);
        Assert.IsFalse(plan.Policy.ExecutionAllowed);
        Assert.IsTrue(plan.Policy.PreserveOriginal);
        Assert.IsTrue(plan.Policy.PreserveSha256);
        CollectionAssert.Contains(plan.Steps.ToArray(), ArtifactInspectionStep.DetectFormatByMagic);
        CollectionAssert.Contains(plan.Steps.ToArray(), ArtifactInspectionStep.ExtractReadOnly);
        CollectionAssert.Contains(plan.Steps.ToArray(), ArtifactInspectionStep.ScanNestedArtifacts);
    }

    [TestMethod]
    public void EncryptedArtifactStopsBehindCryptographicAuthorization()
    {
        var route = TargetIntakeRouter.ClassifyArtifact(Path.Combine(Path.GetTempPath(), "sample.7z"));
        var plan = ArtifactInspectionPlanner.Create(route, encrypted: true);

        Assert.IsTrue(plan.RequiresCryptographicAuthorization);
        CollectionAssert.Contains(plan.Steps.ToArray(), ArtifactInspectionStep.IdentifyEncryption);
        CollectionAssert.Contains(plan.Steps.ToArray(), ArtifactInspectionStep.RequireCryptographicAuthorization);
    }

    [TestMethod]
    public void LiveEndpointCannotEnterOfflineArtifactPlanner()
    {
        var route = TargetIntakeRouter.ClassifyOnline(new Uri("https://example.test"));

        Assert.ThrowsExactly<ArgumentException>(() => ArtifactInspectionPlanner.Create(route, encrypted: false));
    }

    [TestMethod]
    public void UnsafeQuarantinePolicyIsRejected()
    {
        var route = TargetIntakeRouter.ClassifyArtifact(Path.Combine(Path.GetTempPath(), "sample.pdf"));
        var unsafePolicy = ArtifactQuarantinePolicy.Default with { ExecutionAllowed = true };

        Assert.ThrowsExactly<ArgumentException>(() =>
            ArtifactInspectionPlanner.Create(route, encrypted: false, unsafePolicy));
    }
}
