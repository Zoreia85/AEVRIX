using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Aevrix.Remote.Capabilities;

public interface IAppContainerLoopbackPolicyInspector
{
    int GetLoopbackExemptionCount();
}

/// <summary>
/// Reads the Windows global AppContainer loopback exemption table. AEVRIX uses this as a
/// conservative fail-closed guard for Network=None: if any loopback exemption is configured,
/// the zero-capability backend refuses to attest no-network isolation. This deliberately accepts
/// false negatives rather than assuming an exemption belongs to a different container.
/// </summary>
public sealed class WindowsAppContainerLoopbackPolicyInspector : IAppContainerLoopbackPolicyInspector
{
    private const uint ErrorSuccess = 0;

    public int GetLoopbackExemptionCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer loopback policy inspection requires Windows.");
        }

        var status = NativeMethods.NetworkIsolationGetAppContainerConfig(out var count, out var entries);
        if (status != ErrorSuccess)
        {
            throw new Win32Exception(checked((int)status), "Could not read the Windows AppContainer loopback exemption table.");
        }

        try
        {
            return checked((int)count);
        }
        finally
        {
            FreeEntries(count, entries);
        }
    }

    private static void FreeEntries(uint count, IntPtr entries)
    {
        if (entries == IntPtr.Zero)
        {
            return;
        }

        var heap = NativeMethods.GetProcessHeap();
        var stride = Marshal.SizeOf<SidAndAttributes>();
        for (var index = 0u; index < count; index++)
        {
            var entry = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(entries, checked((int)(index * (uint)stride))));
            if (entry.Sid != IntPtr.Zero)
            {
                _ = NativeMethods.HeapFree(heap, 0, entry.Sid);
            }
        }

        _ = NativeMethods.HeapFree(heap, 0, entries);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    private static class NativeMethods
    {
        [DllImport("Firewallapi.dll")]
        internal static extern uint NetworkIsolationGetAppContainerConfig(out uint count, out IntPtr appContainerSids);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);
    }
}
