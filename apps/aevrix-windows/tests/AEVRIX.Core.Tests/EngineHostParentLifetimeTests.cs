using System.Diagnostics;
using Aevrix.Core;
using Aevrix.EngineHost;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class EngineHostParentLifetimeTests
{
    [TestMethod]
    public async Task EngineHost_ExitsWhenDeclaredSupervisingParentExits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parentStartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        parentStartInfo.ArgumentList.Add("-NoProfile");
        parentStartInfo.ArgumentList.Add("-NonInteractive");
        parentStartInfo.ArgumentList.Add("-Command");
        parentStartInfo.ArgumentList.Add("Start-Sleep -Milliseconds 900");

        using var parent = Process.Start(parentStartInfo)
            ?? throw new InvalidOperationException("Unable to start parent lifetime fixture.");

        var pipeName = $"{EngineProtocol.PipeNamePrefix}{Guid.NewGuid():N}";
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        var engineStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        engineStartInfo.ArgumentList.Add(engineAssembly);
        engineStartInfo.Environment[EngineProtocol.PipeEnvironmentVariable] = pipeName;
        engineStartInfo.Environment[EngineProtocol.TokenEnvironmentVariable] = token;
        engineStartInfo.Environment[EngineProtocol.ParentProcessIdEnvironmentVariable] = parent.Id.ToString();

        using var engine = Process.Start(engineStartInfo)
            ?? throw new InvalidOperationException("Unable to start AEVRIX.EngineHost fixture.");

        try
        {
            await parent.WaitForExitAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await engine.WaitForExitAsync(timeout.Token);

            Assert.IsTrue(engine.HasExited, "EngineHost must not survive its supervising parent.");
        }
        finally
        {
            if (!engine.HasExited)
            {
                engine.Kill(entireProcessTree: true);
                await engine.WaitForExitAsync();
            }

            if (!parent.HasExited)
            {
                parent.Kill(entireProcessTree: true);
                await parent.WaitForExitAsync();
            }
        }
    }
}
