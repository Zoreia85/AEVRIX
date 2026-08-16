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

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await supervisor.StartAsync());

        Assert.IsFalse(supervisor.IsRunning);
        Assert.IsNull(supervisor.ProcessId);
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
}
