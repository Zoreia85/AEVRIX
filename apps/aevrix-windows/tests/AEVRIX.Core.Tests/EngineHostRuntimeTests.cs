using Aevrix.Core;
using Aevrix.EngineHost;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class EngineHostRuntimeTests
{
    [TestMethod]
    public async Task DispatchAsync_PingReturnsPong()
    {
        using var temp = new TemporaryDirectory();
        var runtime = new EngineHostRuntime(PathsFor(temp.Path));

        var response = await runtime.DispatchAsync(new EnginePingCommand("req-1"));

        Assert.IsTrue(response.Success);
        Assert.AreEqual("pong", response.Code);
        Assert.AreEqual(EngineProtocol.CurrentVersion, response.ProtocolVersion);
    }

    [TestMethod]
    public async Task DispatchAsync_UnpromotedCaptureCommandFailsClosed()
    {
        using var temp = new TemporaryDirectory();
        var runtime = new EngineHostRuntime(PathsFor(temp.Path));

        var response = await runtime.DispatchAsync(new StopCaptureCommand("req-2", "capture-123"));

        Assert.IsFalse(response.Success);
        Assert.AreEqual("command_not_implemented", response.Code);
    }

    [TestMethod]
    public void DeserializeRequest_ParsesPolymorphicCommand()
    {
        var json = """
        {
          "Token": "01234567890123456789012345678901",
          "Command": {
            "type": "ping",
            "RequestId": "req-3",
            "ProtocolVersion": 3
          }
        }
        """;

        var request = EngineHostRuntime.DeserializeRequest(json);

        Assert.AreEqual("01234567890123456789012345678901", request.Token);
        Assert.IsInstanceOfType(request.Command, typeof(EnginePingCommand));
    }

    [TestMethod]
    public void TokenMatches_RequiresExactNonEmptyValue()
    {
        const string token = "01234567890123456789012345678901";

        Assert.IsTrue(EngineHostRuntime.TokenMatches(token, token));
        Assert.IsFalse(EngineHostRuntime.TokenMatches(token, token + "x"));
        Assert.IsFalse(EngineHostRuntime.TokenMatches(token, string.Empty));
    }

    [TestMethod]
    public void DeserializeRequest_RejectsOversizedPayload()
    {
        var oversized = new string('a', EngineProtocol.MaxMessageBytes + 1);

        Assert.Throws<InvalidDataException>(() => EngineHostRuntime.DeserializeRequest(oversized));
    }

    private static AevrixDataPaths PathsFor(string root) => new(
        UserRoot: root,
        ProjectsRoot: System.IO.Path.Combine(root, "Projects"),
        VaultRoot: System.IO.Path.Combine(root, "Vault"),
        BrowserProfilesRoot: System.IO.Path.Combine(root, "BrowserProfiles"),
        EngineRoot: System.IO.Path.Combine(root, "Engine"),
        UpdatesRoot: System.IO.Path.Combine(root, "Updates"),
        LogsRoot: System.IO.Path.Combine(root, "Logs"),
        CacheRoot: System.IO.Path.Combine(root, "Cache"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-enginehost-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup only.
            }
        }
    }
}
