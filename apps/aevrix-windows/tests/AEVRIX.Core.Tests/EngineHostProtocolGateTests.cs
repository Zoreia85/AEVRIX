using Aevrix.Core;
using Aevrix.EngineHost;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class EngineHostProtocolGateTests
{
    [TestMethod]
    public async Task DispatchAsync_RejectsProtocolVersionMismatch()
    {
        using var temp = new TemporaryDirectory();
        var runtime = new EngineHostRuntime(new AevrixDataPaths(temp.Path));
        EngineCommand command = new EnginePingCommand("req-version") with
        {
            ProtocolVersion = EngineProtocol.CurrentVersion + 1
        };

        var response = await runtime.DispatchAsync(command);

        Assert.IsFalse(response.Success);
        Assert.AreEqual("protocol_version_mismatch", response.Code);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-enginehost-protocol-tests",
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
