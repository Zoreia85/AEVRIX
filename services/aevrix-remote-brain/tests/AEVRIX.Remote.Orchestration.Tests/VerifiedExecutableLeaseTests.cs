using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class VerifiedExecutableLeaseTests
{
    [TestMethod]
    public void FromVerifiedStream_PreservesExactStreamAndWindowsIdentity()
    {
        RequireWindows();
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "verified.exe");
        File.WriteAllBytes(path, [0x41, 0x45, 0x56, 0x52, 0x49, 0x58]);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expectedIdentity = WindowsFileIdentity.FromHandle(stream.SafeFileHandle);

        using var lease = VerifiedExecutableLease.FromVerifiedStream(stream);

        Assert.AreSame(stream, lease.Stream);
        Assert.AreEqual(expectedIdentity, lease.WindowsIdentity);
        Assert.IsFalse(lease.Stream.SafeFileHandle.IsClosed);
    }

    [TestMethod]
    public void Dispose_ClosesOwnedVerifiedStream()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "dispose.exe");
        File.WriteAllBytes(path, [0x01]);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var lease = VerifiedExecutableLease.FromVerifiedStream(stream);
        lease.Dispose();

        Assert.IsTrue(stream.SafeFileHandle.IsClosed);
    }

    [TestMethod]
    public void FromVerifiedStream_RejectsClosedHandle()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "closed.exe");
        File.WriteAllBytes(path, [0x01]);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Dispose();

        Assert.Throws<ArgumentException>(() => VerifiedExecutableLease.FromVerifiedStream(stream));
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Verified executable Windows identity requires the Windows CI runner.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-verified-executable-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
