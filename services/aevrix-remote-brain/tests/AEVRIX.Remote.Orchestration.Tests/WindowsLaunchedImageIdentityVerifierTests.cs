using System.Diagnostics;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsLaunchedImageIdentityVerifierTests
{
    [TestMethod]
    public void CurrentProcessImageIdentity_MatchesIdentityOpenedFromProcessPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        var processPath = Environment.ProcessPath;
        Assert.IsFalse(string.IsNullOrWhiteSpace(processPath));

        using var image = new FileStream(
            processPath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var expected = WindowsFileIdentity.FromHandle(image.SafeFileHandle);
        using var process = Process.GetCurrentProcess();

        WindowsLaunchedImageIdentityVerifier.VerifyMatches(process.Handle, expected);
        Assert.AreEqual(expected, WindowsLaunchedImageIdentityVerifier.GetLaunchedImageIdentity(process.Handle));
    }

    [TestMethod]
    public void DifferentFileIdentity_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        var temp = Path.GetTempFileName();
        try
        {
            using var unrelated = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var unrelatedIdentity = WindowsFileIdentity.FromHandle(unrelated.SafeFileHandle);
            using var process = Process.GetCurrentProcess();

            Assert.Throws<InvalidDataException>(() =>
                WindowsLaunchedImageIdentityVerifier.VerifyMatches(process.Handle, unrelatedIdentity));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [TestMethod]
    public void InvalidProcessHandle_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Throws<ArgumentException>(() =>
            WindowsLaunchedImageIdentityVerifier.GetLaunchedImageIdentity(IntPtr.Zero));
    }

    [TestMethod]
    public void MissingAuthenticatedIdentity_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var process = Process.GetCurrentProcess();
        Assert.Throws<InvalidOperationException>(() =>
            WindowsLaunchedImageIdentityVerifier.VerifyMatches(process.Handle, null));
    }
}
