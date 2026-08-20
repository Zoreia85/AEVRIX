using Aevrix.EngineHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class EngineHostInvestigationRuntimeTests
{
    [TestMethod]
    public async Task Dispatch_RegisterListAndReconcileUseSameDurableMission()
    {
        var root = CreateTempRoot();
        try
        {
            var runtime = new EngineHostRuntime(CreatePaths(root));
            var investigationId = Guid.NewGuid();

            var register = await runtime.DispatchAsync(new RegisterInvestigationRuntimeCommand(
                Guid.NewGuid().ToString("N"),
                investigationId,
                "engine-runtime-test",
                InvestigationTargetKind.WebSystem,
                InvestigationStrategy.Investigate,
                "authorized",
                InvestigationPriority.Normal,
                []));

            Assert.IsTrue(register.Success);
            Assert.AreEqual("investigation_registered", register.Code);
            var registered = register.Data as InvestigationRuntimeRecord;
            Assert.IsNotNull(registered);
            Assert.AreEqual(investigationId, registered.InvestigationId);

            var list = await runtime.DispatchAsync(new ListInvestigationRuntimeCommand(Guid.NewGuid().ToString("N")));
            Assert.IsTrue(list.Success);
            var listed = list.Data as IReadOnlyList<InvestigationRuntimeRecord>;
            Assert.IsNotNull(listed);
            Assert.AreEqual(1, listed.Count);
            Assert.AreEqual(registered.MissionId, listed[0].MissionId);

            var reconcile = await runtime.DispatchAsync(new ReconcileInvestigationScheduleCommand(Guid.NewGuid().ToString("N")));
            Assert.IsTrue(reconcile.Success);
            var reconciled = reconcile.Data as IReadOnlyList<InvestigationRuntimeRecord>;
            Assert.IsNotNull(reconciled);
            var current = reconciled.Single(item => item.InvestigationId == investigationId);
            Assert.AreEqual(registered.MissionId, current.MissionId);
            Assert.AreEqual(InvestigationRunState.Blocked, current.State);
            Assert.AreEqual(InvestigationPhase.Acquisition, current.CurrentPhase);
            Assert.IsNull(current.EstimatedRemaining);
            StringAssert.Contains(current.Blocker!, "adapter");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task Dispatch_InvalidExecutableRegistrationFailsClosedWithoutCreatingRuntimeRecord()
    {
        var root = CreateTempRoot();
        try
        {
            var runtime = new EngineHostRuntime(CreatePaths(root));
            var response = await runtime.DispatchAsync(new RegisterInvestigationRuntimeCommand(
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid(),
                "invalid-executable",
                InvestigationTargetKind.DesktopApplication,
                InvestigationStrategy.InvestigateAndEmulate,
                "authorized",
                InvestigationPriority.Normal,
                []));

            Assert.IsFalse(response.Success);
            Assert.AreEqual("investigation_registration_blocked", response.Code);

            var list = await runtime.DispatchAsync(new ListInvestigationRuntimeCommand(Guid.NewGuid().ToString("N")));
            var records = list.Data as IReadOnlyList<InvestigationRuntimeRecord>;
            Assert.IsNotNull(records);
            Assert.AreEqual(0, records.Count);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "AEVRIX-ENGINE-RUNTIME-TEST-" + Guid.NewGuid().ToString("N"));
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
            // Cleanup is best effort; assertions above remain authoritative.
        }
    }
}
