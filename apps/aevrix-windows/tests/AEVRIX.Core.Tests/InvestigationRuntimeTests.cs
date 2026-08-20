using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class InvestigationRuntimeTests
{
    [TestMethod]
    public async Task RegisterAsync_FingerprintsArtifactWithoutPersistingLocalPath()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = CreatePaths(root);
            var artifactPath = Path.Combine(root, "sensitive-input", "setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllTextAsync(artifactPath, "authorized sample payload");

            var coordinator = new InvestigationRuntimeCoordinator(paths);
            var record = await coordinator.RegisterAsync(new InvestigationRuntimeRegistration(
                Guid.NewGuid(),
                "runtime-test",
                InvestigationTargetKind.DesktopApplication,
                InvestigationStrategy.InvestigateAndEmulate,
                "authorized",
                InvestigationPriority.Normal,
                [new InvestigationInputArtifact("setup.exe", artifactPath)]));

            Assert.AreEqual(InvestigationRunState.Ready, record.State);
            Assert.AreEqual(InvestigationPhase.Acquisition, record.CurrentPhase);
            Assert.IsTrue(record.PercentComplete > 0);
            Assert.AreEqual(1, record.Artifacts.Count);
            Assert.AreEqual(64, record.Artifacts[0].Sha256.Length);

            var storePath = Path.Combine(paths.EngineRoot, "investigation-runtime.json");
            var storedJson = await File.ReadAllTextAsync(storePath);
            Assert.IsFalse(storedJson.Contains(artifactPath, StringComparison.Ordinal));
            Assert.IsFalse(storedJson.Contains("sensitive-input", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task ReconcileScheduleAsync_BlocksAtRealAdapterBoundaryInsteadOfSimulatingWork()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = CreatePaths(root);
            var coordinator = new InvestigationRuntimeCoordinator(paths);
            var registered = await coordinator.RegisterAsync(new InvestigationRuntimeRegistration(
                Guid.NewGuid(),
                "web-runtime-test",
                InvestigationTargetKind.WebSystem,
                InvestigationStrategy.Investigate,
                "owned",
                InvestigationPriority.High,
                []));

            var reconciled = await coordinator.ReconcileScheduleAsync(
                new LocalCapacityRecommendation(8, 16L * 1024 * 1024 * 1024, 2, "test"));
            var runtime = reconciled.Single(item => item.InvestigationId == registered.InvestigationId);

            Assert.AreEqual(InvestigationRunState.Blocked, runtime.State);
            Assert.AreEqual(InvestigationPhase.Acquisition, runtime.CurrentPhase);
            Assert.AreEqual(registered.PercentComplete, runtime.PercentComplete);
            Assert.IsNull(runtime.EstimatedRemaining);
            StringAssert.Contains(runtime.Blocker!, "adapter");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task ReconcileScheduleAsync_UsesTenSlotsAndQueuesOverflowBeforeAdapterBoundary()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = CreatePaths(root);
            var coordinator = new InvestigationRuntimeCoordinator(paths);
            for (var index = 0; index < 12; index++)
            {
                await coordinator.RegisterAsync(new InvestigationRuntimeRegistration(
                    Guid.NewGuid(),
                    $"web-{index:D2}",
                    InvestigationTargetKind.WebSystem,
                    InvestigationStrategy.Investigate,
                    "authorized",
                    InvestigationPriority.Normal,
                    []));
            }

            var reconciled = await coordinator.ReconcileScheduleAsync(
                new LocalCapacityRecommendation(64, 128L * 1024 * 1024 * 1024, 10, "test"));

            Assert.AreEqual(10, reconciled.Count(item => item.State == InvestigationRunState.Blocked));
            Assert.AreEqual(2, reconciled.Count(item => item.State == InvestigationRunState.Queued));
            Assert.IsTrue(reconciled.Where(item => item.State == InvestigationRunState.Queued)
                .All(item => item.QueuePosition > 0));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task ListAsync_PersistsAcrossCoordinatorInstances()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = CreatePaths(root);
            var investigationId = Guid.NewGuid();
            var first = new InvestigationRuntimeCoordinator(paths);
            await first.RegisterAsync(new InvestigationRuntimeRegistration(
                investigationId,
                "persisted-runtime",
                InvestigationTargetKind.ApiService,
                InvestigationStrategy.Investigate,
                "owned",
                InvestigationPriority.Normal,
                []));

            var second = new InvestigationRuntimeCoordinator(paths);
            var loaded = await second.ListAsync();

            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(investigationId, loaded[0].InvestigationId);
            Assert.AreEqual("persisted-runtime", loaded[0].Workspace);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task RegisterAsync_FingerprintMatchesRealArtifactBytes()
    {
        var root = CreateTempRoot();
        try
        {
            var paths = CreatePaths(root);
            var artifactPath = Path.Combine(root, "sample.bin");
            var bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
            await File.WriteAllBytesAsync(artifactPath, bytes);
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            var coordinator = new InvestigationRuntimeCoordinator(paths);
            var record = await coordinator.RegisterAsync(new InvestigationRuntimeRegistration(
                Guid.NewGuid(),
                "hash-test",
                InvestigationTargetKind.DesktopApplication,
                InvestigationStrategy.Investigate,
                "owned",
                InvestigationPriority.Normal,
                [new InvestigationInputArtifact("sample.bin", artifactPath)]));

            Assert.AreEqual(expected, record.Artifacts.Single().Sha256);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "AEVRIX-RUNTIME-TEST-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static AevrixDataPaths CreatePaths(string root)
        => new(
            root,
            Path.Combine(root, "Projects"),
            Path.Combine(root, "Vault"),
            Path.Combine(root, "BrowserProfiles"),
            Path.Combine(root, "Engine"),
            Path.Combine(root, "Updates"),
            Path.Combine(root, "Logs"),
            Path.Combine(root, "Cache"));

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must not hide the actual assertion outcome.
        }
    }
}
