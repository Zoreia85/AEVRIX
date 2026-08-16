using System.Diagnostics;
using Aevrix.Core;
using Aevrix.EngineHost;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class EngineHostIpcIntegrationTests
{
    [TestMethod]
    public async Task SendAsync_PingTraversesRealEngineHostNamedPipe()
    {
        var pipeName = $"{EngineProtocol.PipeNamePrefix}{Guid.NewGuid():N}";
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            + Convert.ToHexString(Guid.NewGuid().ToByteArray());

        using var process = StartEngineHost(pipeName, token);
        try
        {
            var client = new EngineHostClient(pipeName, token, TimeSpan.FromSeconds(15));
            var requestId = Guid.NewGuid().ToString("N");

            var response = await client.SendAsync(new EnginePingCommand(requestId));

            Assert.IsTrue(response.Success);
            Assert.AreEqual(requestId, response.RequestId);
            Assert.AreEqual("pong", response.Code);
            Assert.AreEqual(EngineProtocol.CurrentVersion, response.ProtocolVersion);
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    [TestMethod]
    public async Task SendAsync_WrongTokenFailsClosedOverRealEngineHostNamedPipe()
    {
        var pipeName = $"{EngineProtocol.PipeNamePrefix}{Guid.NewGuid():N}";
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var wrongToken = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            + Convert.ToHexString(Guid.NewGuid().ToByteArray());

        using var process = StartEngineHost(pipeName, token);
        try
        {
            var client = new EngineHostClient(pipeName, wrongToken, TimeSpan.FromSeconds(15));
            var requestId = Guid.NewGuid().ToString("N");

            var response = await client.SendAsync(new EnginePingCommand(requestId));

            Assert.IsFalse(response.Success);
            Assert.AreEqual(requestId, response.RequestId);
            Assert.AreEqual("unauthorized", response.Code);
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    [TestMethod]
    public void Constructor_RejectsInvalidPipeAndWeakToken()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new EngineHostClient("other.pipe", new string('a', 32)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new EngineHostClient($"{EngineProtocol.PipeNamePrefix}valid", "too-short"));
    }

    private static Process StartEngineHost(string pipeName, string token)
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(engineAssembly);
        startInfo.Environment[EngineProtocol.PipeEnvironmentVariable] = pipeName;
        startInfo.Environment[EngineProtocol.TokenEnvironmentVariable] = token;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start AEVRIX.EngineHost.");
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
}
