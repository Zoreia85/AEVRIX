using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class DesktopFirstRunReadinessTests
{
    [TestMethod]
    public void Evaluate_LocalSupervised_AllowsCompletionOnlyWithRealLocalGates()
    {
        var evaluation = DesktopFirstRunReadiness.Evaluate(new DesktopFirstRunSignals(
            StructuralIntegrityAttempted: true,
            StructuralIntegrityVerified: true,
            EngineHostVerificationAttempted: true,
            EngineHostAuthenticated: true,
            DeviceSecurityTier: DeviceKeySecurityTier.TpmNonExportable,
            DeviceCertificateValidated: false,
            RemoteEndpointConfigured: false,
            RemoteSessionAuthenticated: false,
            RequestedMode: DesktopOperatingMode.LocalSupervised,
            PermissionsAcknowledged: true));

        Assert.IsTrue(evaluation.CanComplete);
        Assert.AreEqual(DesktopReadinessStatus.Ready, evaluation.Gate("remote-identity").Status);
    }

    [TestMethod]
    public void Evaluate_LocalSupervised_FailsClosedWhenEngineProofIsLost()
    {
        var evaluation = DesktopFirstRunReadiness.Evaluate(new DesktopFirstRunSignals(
            StructuralIntegrityAttempted: true,
            StructuralIntegrityVerified: true,
            EngineHostVerificationAttempted: true,
            EngineHostAuthenticated: false,
            DeviceSecurityTier: DeviceKeySecurityTier.TpmNonExportable,
            DeviceCertificateValidated: false,
            RemoteEndpointConfigured: false,
            RemoteSessionAuthenticated: false,
            RequestedMode: DesktopOperatingMode.LocalSupervised,
            PermissionsAcknowledged: true));

        Assert.IsFalse(evaluation.CanComplete);
        Assert.AreEqual(DesktopReadinessStatus.Blocked, evaluation.Gate("enginehost").Status);
    }

    [TestMethod]
    public void Evaluate_RemoteGoverned_RequiresEndpointCertificateAndSession()
    {
        var blocked = DesktopFirstRunReadiness.Evaluate(new DesktopFirstRunSignals(
            StructuralIntegrityAttempted: true,
            StructuralIntegrityVerified: true,
            EngineHostVerificationAttempted: true,
            EngineHostAuthenticated: true,
            DeviceSecurityTier: DeviceKeySecurityTier.TpmNonExportable,
            DeviceCertificateValidated: false,
            RemoteEndpointConfigured: true,
            RemoteSessionAuthenticated: false,
            RequestedMode: DesktopOperatingMode.RemoteGoverned,
            PermissionsAcknowledged: true));

        Assert.IsFalse(blocked.CanComplete);
        Assert.AreEqual(DesktopReadinessStatus.Blocked, blocked.Gate("remote-identity").Status);

        var ready = DesktopFirstRunReadiness.Evaluate(new DesktopFirstRunSignals(
            StructuralIntegrityAttempted: true,
            StructuralIntegrityVerified: true,
            EngineHostVerificationAttempted: true,
            EngineHostAuthenticated: true,
            DeviceSecurityTier: DeviceKeySecurityTier.TpmNonExportable,
            DeviceCertificateValidated: true,
            RemoteEndpointConfigured: true,
            RemoteSessionAuthenticated: true,
            RequestedMode: DesktopOperatingMode.RemoteGoverned,
            PermissionsAcknowledged: true));

        Assert.IsTrue(ready.CanComplete);
    }

    [TestMethod]
    public void ProfileStore_RoundTripsPrivacySafeFirstRunState()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = System.IO.Path.Combine(root, "desktop-first-run.json");
            var store = new DesktopFirstRunProfileStore(path);
            var profile = store.LoadOrCreate();
            var updated = profile with
            {
                RequestedMode = DesktopOperatingMode.LocalSupervised,
                PermissionsAcknowledged = true,
                CompletedAtUtc = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero)
            };

            store.Save(updated);
            var loaded = store.LoadOrCreate();

            Assert.AreEqual(updated.InstallationId, loaded.InstallationId);
            Assert.AreEqual(DesktopOperatingMode.LocalSupervised, loaded.RequestedMode);
            Assert.IsTrue(loaded.PermissionsAcknowledged);
            Assert.AreEqual(updated.CompletedAtUtc, loaded.CompletedAtUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Profile_RejectsInconsistentCompletedState()
    {
        var invalid = DesktopFirstRunProfile.CreateNew() with
        {
            CompletedAtUtc = DateTimeOffset.UtcNow
        };

        Assert.ThrowsExactly<InvalidDataException>(() => invalid.Validate());
    }

    [TestMethod]
    public void ProfileStore_RejectsCorruptJsonInsteadOfTrustingIt()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = System.IO.Path.Combine(root, "desktop-first-run.json");
            File.WriteAllText(path, "{broken-json");
            var store = new DesktopFirstRunProfileStore(path);

            Assert.ThrowsExactly<InvalidDataException>(() => store.LoadOrCreate());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void IntegrityProbe_HashesRequiredFilesAndRejectsDuplicateRolesByPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var desktop = System.IO.Path.Combine(root, "AEVRIX.Desktop.dll");
            var engine = System.IO.Path.Combine(root, "AEVRIX.EngineHost.dll");
            File.WriteAllBytes(desktop, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(engine, new byte[] { 5, 6, 7, 8 });

            var result = DesktopLocalIntegrityProbe.Probe(
                ("Desktop", desktop),
                ("EngineHost", engine));

            Assert.IsTrue(result.Verified);
            Assert.AreEqual(2, result.Artifacts.Count);
            Assert.AreEqual(64, result.Artifacts[0].Sha256.Length);

            var duplicate = DesktopLocalIntegrityProbe.Probe(
                ("Desktop", desktop),
                ("EngineHost", desktop));

            Assert.IsFalse(duplicate.Verified);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-first-run-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
