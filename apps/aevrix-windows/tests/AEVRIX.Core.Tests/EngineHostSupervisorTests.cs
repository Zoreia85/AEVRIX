using System.Diagnostics;
using Aevrix.Core;
using Aevrix.EngineHost;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class EngineHostSupervisorTests
{
    [TestMethod]
    public async Task StartAsync_AuthenticatesRealEngineHostAndStopsCleanly()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        await using var supervisor = new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20),
            requestTimeout: TimeSpan.FromSeconds(5));

        await supervisor.StartAsync();

        Assert.IsTrue(supervisor.IsRunning);
        Assert.IsNotNull(supervisor.ProcessId);

        var requestId = Guid.NewGuid().ToString("N");
        var response = await supervisor.SendAsync(new EnginePingCommand(requestId));

        Assert.IsTrue(response.Success);
        Assert.AreEqual("pong", response.Code);
        Assert.AreEqual(requestId, response.RequestId);

        await supervisor.StopAsync();

        Assert.IsFalse(supervisor.IsRunning);
        Assert.IsNull(supervisor.ProcessId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await supervisor.SendAsync(new EnginePingCommand(Guid.NewGuid().ToString("N"))));
    }

    [TestMethod]
    public async Task StartAsync_AllowsImmediateSequentialAuthenticatedRequests()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        await using var supervisor = new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20),
            requestTimeout: TimeSpan.FromSeconds(5));

        await supervisor.StartAsync();

        for (var index = 0; index < 32; index++)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var response = await supervisor.SendAsync(new EnginePingCommand(requestId));

            Assert.IsTrue(response.Success);
            Assert.AreEqual("pong", response.Code);
            Assert.AreEqual(requestId, response.RequestId);
        }

        Assert.IsTrue(supervisor.IsRunning);
    }

    [TestMethod]
    public async Task StopAsync_AllowsAuthenticatedRestart()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        await using var supervisor = new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20),
            requestTimeout: TimeSpan.FromSeconds(5));

        await supervisor.StartAsync();
        var firstRequestId = Guid.NewGuid().ToString("N");
        var first = await supervisor.SendAsync(new EnginePingCommand(firstRequestId));
        Assert.IsTrue(first.Success);
        Assert.AreEqual("pong", first.Code);
        Assert.AreEqual(firstRequestId, first.RequestId);

        await supervisor.StopAsync();
        Assert.IsFalse(supervisor.IsRunning);
        Assert.IsNull(supervisor.ProcessId);

        await supervisor.StartAsync();
        var secondRequestId = Guid.NewGuid().ToString("N");
        var second = await supervisor.SendAsync(new EnginePingCommand(secondRequestId));

        Assert.IsTrue(second.Success);
        Assert.AreEqual("pong", second.Code);
        Assert.AreEqual(secondRequestId, second.RequestId);
        Assert.IsTrue(supervisor.IsRunning);
        Assert.IsNotNull(supervisor.ProcessId);
    }

    [TestMethod]
    public async Task StopAsync_IsIdempotentAfterCleanStop()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        await using var supervisor = new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20),
            requestTimeout: TimeSpan.FromSeconds(5));

        await supervisor.StartAsync();
        await supervisor.StopAsync();
        await supervisor.StopAsync();

        Assert.IsFalse(supervisor.IsRunning);
        Assert.IsNull(supervisor.ProcessId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await supervisor.SendAsync(new EnginePingCommand(Guid.NewGuid().ToString("N"))));
    }

    [TestMethod]
    public async Task StartAsync_RecoversAfterUnexpectedEngineHostExit()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        await using var supervisor = new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20),
            requestTimeout: TimeSpan.FromSeconds(5));

        await supervisor.StartAsync();
        var processId = supervisor.ProcessId;
        Assert.IsNotNull(processId);

        await KillProcessAsync(processId.Value);

        Assert.IsFalse(supervisor.IsRunning);
        Assert.IsNull(supervisor.ProcessId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await supervisor.SendAsync(new EnginePingCommand(Guid.NewGuid().ToString("N"))));

        await supervisor.StartAsync();
        var requestId = Guid.NewGuid().ToString("N");
        var response = await supervisor.SendAsync(new EnginePingCommand(requestId));

        Assert.IsTrue(response.Success);
        Assert.AreEqual("pong", response.Code);
        Assert.AreEqual(requestId, response.RequestId);
        Assert.IsTrue(supervisor.IsRunning);
        Assert.IsNotNull(supervisor.ProcessId);
    }

    [TestMethod]
    public async Task StartAsync_IsIdempotentWhileHealthy()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        await using var supervisor = new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20));

        await supervisor.StartAsync();
        var firstProcessId = supervisor.ProcessId;

        await supervisor.StartAsync();

        Assert.AreEqual(firstProcessId, supervisor.ProcessId);
        Assert.IsTrue(supervisor.IsRunning);
    }

    [TestMethod]
    public async Task StartAsync_FailsClosedWhenChildCannotBecomeEngineHost()
    {
        await using var supervisor = new EngineHostSupervisor(
            "dotnet",
            new[] { "--info" },
            startupTimeout: TimeSpan.FromSeconds(5),
            requestTimeout: TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsExactlyAsync<TimeoutException>(async () =>
            await supervisor.StartAsync());

        Assert.IsFalse(supervisor.IsRunning);
        Assert.IsNull(supervisor.ProcessId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await supervisor.SendAsync(new EnginePingCommand(Guid.NewGuid().ToString("N"))));
    }

    [TestMethod]
    public async Task DisposeAsync_TerminatesOwnedEngineHostAndRejectsReuse()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        var supervisor = new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20));

        await supervisor.StartAsync();
        Assert.IsTrue(supervisor.IsRunning);

        await supervisor.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await supervisor.StartAsync());
    }

    private static async Task KillProcessAsync(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }
}
