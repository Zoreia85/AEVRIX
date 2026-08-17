using Microsoft.Win32.SafeHandles;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsFileIdentityTests
{
    [TestMethod]
    public void FromHandle_ReturnsSameIdentityForSameOpenFile()
    {
        RequireWindows();
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "identity.bin");
        File.WriteAllText(path, "aevrix-identity");

        using var first = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var second = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var firstIdentity = WindowsFileIdentity.FromHandle(first.SafeFileHandle);
        var secondIdentity = WindowsFileIdentity.FromHandle(second.SafeFileHandle);

        Assert.AreEqual(firstIdentity, secondIdentity);
        Assert.AreNotEqual(0UL, firstIdentity.VolumeSerialNumber);
        Assert.AreEqual(32, firstIdentity.FileIdHex.Length);
    }

    [TestMethod]
    public void FromHandle_RemainsStableAcrossRenameWhileHandleIsOpen()
    {
        RequireWindows();
        using var temp = new TempDirectory();
        var original = Path.Combine(temp.Path, "before.bin");
        var renamed = Path.Combine(temp.Path, "after.bin");
        File.WriteAllText(original, "aevrix-rename");

        using var stream = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var before = WindowsFileIdentity.FromHandle(stream.SafeFileHandle);

        File.Move(original, renamed);
        using var reopened = new FileStream(renamed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var after = WindowsFileIdentity.FromHandle(reopened.SafeFileHandle);

        Assert.AreEqual(before, after,
            "Stable Windows file identity must follow the file object rather than its pathname.");
    }

    [TestMethod]
    public void FromHandle_DistinguishesDifferentFilesWithSameContent()
    {
        RequireWindows();
        using var temp = new TempDirectory();
        var one = Path.Combine(temp.Path, "one.bin");
        var two = Path.Combine(temp.Path, "two.bin");
        File.WriteAllText(one, "same-content");
        File.WriteAllText(two, "same-content");

        using var first = new FileStream(one, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var second = new FileStream(two, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.AreNotEqual(
            WindowsFileIdentity.FromHandle(first.SafeFileHandle),
            WindowsFileIdentity.FromHandle(second.SafeFileHandle),
            "File identity must distinguish different file objects even when their bytes are identical.");
    }

    [TestMethod]
    public void FromHandle_RejectsClosedHandle()
    {
        RequireWindows();
        SafeFileHandle handle;
        using (var temp = new TempDirectory())
        {
            var path = Path.Combine(temp.Path, "closed.bin");
            File.WriteAllText(path, "closed");
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            handle = stream.SafeFileHandle;
            stream.Dispose();
        }

        Assert.Throws<ObjectDisposedException>(() => WindowsFileIdentity.FromHandle(handle));
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file identity tests require the Windows CI runner.");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-file-identity-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
