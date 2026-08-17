using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Resolves the executable image associated with an already-created Windows process and compares
/// its stable file-object identity with the identity captured from the cryptographically verified
/// executable handle. Callers are expected to invoke this while the child primary thread is still
/// suspended; any query/open/identity mismatch is fail-closed.
/// </summary>
internal static class WindowsLaunchedImageIdentityVerifier
{
    private const int MaximumImagePathCharacters = 32_768;

    internal static WindowsFileIdentity GetLaunchedImageIdentity(IntPtr processHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Launched-image identity verification requires Windows.");
        }
        if (processHandle == IntPtr.Zero || processHandle == new IntPtr(-1))
        {
            throw new ArgumentException("A live process handle is required.", nameof(processHandle));
        }

        var capacity = MaximumImagePathCharacters;
        var path = new StringBuilder(capacity);
        if (!NativeMethods.QueryFullProcessImageNameW(processHandle, 0, path, ref capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query the executable image selected for the suspended process.");
        }
        if (capacity <= 0 || capacity > MaximumImagePathCharacters)
        {
            throw new InvalidDataException("Suspended process image path length is invalid.");
        }

        var imagePath = path.ToString();
        if (string.IsNullOrWhiteSpace(imagePath) || !Path.IsPathFullyQualified(imagePath))
        {
            throw new InvalidDataException("Suspended process image path is not a bounded absolute path.");
        }

        using var image = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return WindowsFileIdentity.FromHandle(image.SafeFileHandle);
    }

    internal static void VerifyMatches(IntPtr processHandle, WindowsFileIdentity authenticatedIdentity)
    {
        var launchedIdentity = GetLaunchedImageIdentity(processHandle);
        if (launchedIdentity != authenticatedIdentity)
        {
            throw new InvalidDataException("Suspended process image identity does not match the cryptographically authenticated executable object.");
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageNameW(
            IntPtr processHandle,
            uint flags,
            StringBuilder executablePath,
            ref int size);
    }
}
