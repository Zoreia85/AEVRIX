using Aevrix.Core;
using Aevrix.EngineHost;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class EngineHostLifecycleControllerTests
{
    [TestMethod]
    public async Task StartAsync_RequiresAuthenticatedEngineStatusBeforeReady()
    {
        var session = new FakeSession
        {
            StartMakesRunning = true,
            ProcessIdValue = 4242,
            Response = new EngineResponse("status", true, "engine_ready", "ready")
        };
        await using var controller = new EngineHostLifecycleController(session);

        var snapshot = await controller.StartAsync();

        Assert.AreEqual(EngineHostLifecycleState.Ready, snapshot.State);
        Assert.AreEqual(4242, snapshot.ProcessId);
        Assert.AreEqual("engine_ready", snapshot.Code);
        Assert.AreEqual(1, session.StartCalls);
        Assert.AreEqual(1, session.SendCalls);
        Assert.IsInstanceOfType<GetEngineStatusCommand>(session.LastCommand);
    }

    [TestMethod]
    public async Task StartAsync_StatusFailureFailsClosedAndCleansUpSession()
    {
        var session = new FakeSession
        {
            StartMakesRunning = true,
            ProcessIdValue = 4242,
            Response = new EngineResponse("status", false, "degraded", "not ready")
        };
        await using var controller = new EngineHostLifecycleController(session);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await controller.StartAsync());

        Assert.AreEqual(EngineHostLifecycleState.Faulted, controller.Snapshot.State);
        Assert.AreEqual("engine_start_failed", controller.Snapshot.Code);
        Assert.IsTrue(session.StopCalls >= 1);
        Assert.IsFalse(session.IsRunning);
    }

    [TestMethod]
    public async Task RefreshAsync_ProcessLossAfterReadyBecomesFaulted()
    {
        var session = new FakeSession
        {
            StartMakesRunning = true,
            ProcessIdValue = 4242,
            Response = new EngineResponse("status", true, "engine_ready", "ready")
        };
        await using var controller = new EngineHostLifecycleController(session);
        await controller.StartAsync();
        session.IsRunningValue = false;

        var snapshot = await controller.RefreshAsync();

        Assert.AreEqual(EngineHostLifecycleState.Faulted, snapshot.State);
        Assert.AreEqual("engine_unavailable", snapshot.Code);
        Assert.IsNull(snapshot.ProcessId);
    }

    [TestMethod]
    public async Task StopAsync_TransitionsToStoppedAndClearsProcessIdentity()
    {
        var session = new FakeSession
        {
            StartMakesRunning = true,
            ProcessIdValue = 4242,
            Response = new EngineResponse("status", true, "engine_ready", "ready")
        };
        await using var controller = new EngineHostLifecycleController(session);
        await controller.StartAsync();

        var snapshot = await controller.StopAsync();

        Assert.AreEqual(EngineHostLifecycleState.Stopped, snapshot.State);
        Assert.AreEqual("engine_stopped", snapshot.Code);
        Assert.IsNull(snapshot.ProcessId);
        Assert.IsFalse(snapshot.IsReady);
    }

    [TestMethod]
    public async Task RealEngineHost_StartStatusAndStopProduceEvidenceBackedLifecycle()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        var supervisor = new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20),
            requestTimeout: TimeSpan.FromSeconds(5));
        await using var controller = new EngineHostLifecycleController(
            new EngineHostSupervisorSession(supervisor));

        var ready = await controller.StartAsync();

        Assert.AreEqual(EngineHostLifecycleState.Ready, ready.State);
        Assert.IsTrue(ready.ProcessId > 0);
        Assert.AreEqual("engine_ready", ready.Code);

        var refreshed = await controller.RefreshAsync();
        Assert.AreEqual(EngineHostLifecycleState.Ready, refreshed.State);
        Assert.AreEqual(ready.ProcessId, refreshed.ProcessId);

        var stopped = await controller.StopAsync();
        Assert.AreEqual(EngineHostLifecycleState.Stopped, stopped.State);
        Assert.IsNull(stopped.ProcessId);
    }

    private sealed class FakeSession : IEngineHostSession
    {
        public bool StartMakesRunning { get; init; }
        public bool IsRunningValue { get; set; }
        public int? ProcessIdValue { get; init; }
        public EngineResponse Response { get; init; } =
            new("status", true, "engine_ready", "ready");
        public int StartCalls { get; private set; }
        public int SendCalls { get; private set; }
        public int StopCalls { get; private set; }
        public EngineCommand? LastCommand { get; private set; }

        public bool IsRunning => IsRunningValue;
        public int? ProcessId => IsRunning ? ProcessIdValue : null;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            IsRunningValue = StartMakesRunning;
            return Task.CompletedTask;
        }

        public Task<EngineResponse> SendAsync(
            EngineCommand command,
            CancellationToken cancellationToken = default)
        {
            SendCalls++;
            LastCommand = command;
            return Task.FromResult(Response with { RequestId = command.RequestId });
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            IsRunningValue = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunningValue = false;
            return ValueTask.CompletedTask;
        }
    }
}
