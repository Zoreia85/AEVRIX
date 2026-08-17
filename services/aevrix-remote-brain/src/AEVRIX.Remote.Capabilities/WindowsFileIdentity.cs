using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Stable Windows file-object identity derived from an already-open handle.
/// This deliberately does not trust a pathname: volume serial + 128-bit file ID
/// are used so aliases/hard-links resolve to the same underlying object.
/// </summary>
internal readonly record struct WindowsFileIdentity(ulong VolumeSerialNumber, Guid FileId)
{
    internal static WindowsFileIdentity FromHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Stable file identity requires Windows.");
        }
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException("A live file handle is required.", nameof(handle));
        }

        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out var info,
                (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query stable file identity from the authenticated executable handle.");
        }

        return new WindowsFileIdentity(info.VolumeSerialNumber, new Guid(info.FileId.Identifier));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Identifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(
            SafeFileHandle fileHandle,
            FileInfoByHandleClass fileInformationClass,
            out FileIdInfo fileInformation,
            uint bufferSize);
    }
}
