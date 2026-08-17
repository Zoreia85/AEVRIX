using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class SpecialistLabCoordinationTests
{
    [TestMethod]
    public void HttpsTarget_RoutesToWebOnlineLabWithoutEmbeddingCredentials()
    {
        var route = TargetIntakeRouter.ClassifyWeb(new Uri("https://example.test/app"));

        Assert.AreEqual(SpecialistLab.WebOnline, route.Lab);
        Assert.AreEqual(TargetKind.HttpsWebApplication, route.Kind);
        Assert.AreEqual(RoutingEvidenceStrength.TransportVerified, route.EvidenceStrength);
        Assert.IsTrue(route.IsRoutable);
        Assert.IsTrue(route.RequiresContentVerification);

        Assert.ThrowsExactly<ArgumentException>(() =>
            TargetIntakeRouter.ClassifyWeb(new Uri("https://user:secret@example.test/app")));
    }

    [TestMethod]
    public void MobileArtifacts_RouteAndroidAndAppleToOneMobileLab()
    {
        var cases = new[]
        {
            ("sample.apk", TargetKind.AndroidApk),
            ("sample.aab", TargetKind.AndroidAppBundle),
            ("sample.xapk", TargetKind.AndroidXapk),
            ("sample.ipa", TargetKind.AppleIpa)
        };

        foreach (var (fileName, expectedKind) in cases)
        {
            var route = TargetIntakeRouter.ClassifyArtifact(Path.Combine(Path.GetTempPath(), fileName));

            Assert.AreEqual(SpecialistLab.Mobile, route.Lab, fileName);
            Assert.AreEqual(expectedKind, route.Kind, fileName);
            Assert.AreEqual(RoutingEvidenceStrength.ExtensionHint, route.EvidenceStrength, fileName);
            Assert.IsTrue(route.RequiresContentVerification, fileName);
        }
    }

    [TestMethod]
    public void OfflineArtifacts_RouteToDesktopOfflineWithoutClaimingNativeExecutionSupport()
    {
        var files = new[]
        {
            "sample.exe",
            "sample.msi",
            "sample.msix",
            "sample.dll",
            "sample.jar",
            "sample.dmg",
            "sample.pkg",
            "sample.AppImage",
            "sample.deb",
            "sample.rpm"
        };

        foreach (var fileName in files)
        {
            var route = TargetIntakeRouter.ClassifyArtifact(Path.Combine(Path.GetTempPath(), fileName));

            Assert.AreEqual(SpecialistLab.DesktopOffline, route.Lab, fileName);
            Assert.AreEqual(RoutingEvidenceStrength.ExtensionHint, route.EvidenceStrength, fileName);
            Assert.IsTrue(route.RequiresContentVerification, fileName);
            Assert.IsTrue(route.IsRoutable, fileName);
        }
    }

    [TestMethod]
    public void UnknownArtifact_FailsClosedWithoutChoosingALab()
    {
        var route = TargetIntakeRouter.ClassifyArtifact(Path.Combine(Path.GetTempPath(), "ambiguous.payload"));

        Assert.IsNull(route.Lab);
        Assert.AreEqual(TargetKind.Unknown, route.Kind);
        Assert.AreEqual(RoutingEvidenceStrength.Unknown, route.EvidenceStrength);
        Assert.IsFalse(route.IsRoutable);
        Assert.IsTrue(route.RequiresContentVerification);
    }

    [TestMethod]
    public void CrossLabHandoff_PreservesOwnershipAndOnlyGrantsCandidateEvidenceAuthority()
    {
        var projectId = Guid.NewGuid();
        var request = CrossLabHandoffRequest.Create(
            projectId,
            "TARGET-42",
            SpecialistLab.Mobile,
            SpecialistLab.WebOnline,
            "Inspect the authorized API surface used by the mobile application.",
            new[] { "ev-2", " ev-1 ", "ev-2", "" });

        Assert.AreEqual(projectId, request.ProjectId);
        Assert.AreEqual("target-42", request.TargetId);
        Assert.AreEqual(SpecialistLab.Mobile, request.OwningLab);
        Assert.AreEqual(SpecialistLab.WebOnline, request.DelegatedLab);
        Assert.AreEqual(DelegatedLabAuthority.CandidateEvidenceOnly, request.Authority);
        CollectionAssert.AreEqual(new[] { "ev-1", "ev-2" }, request.EvidenceIds.ToArray());
    }

    [TestMethod]
    public void CrossLabHandoff_RejectsSelfDelegation()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            CrossLabHandoffRequest.Create(
                Guid.NewGuid(),
                "target-1",
                SpecialistLab.WebOnline,
                SpecialistLab.WebOnline,
                "duplicate work"));
    }
}
