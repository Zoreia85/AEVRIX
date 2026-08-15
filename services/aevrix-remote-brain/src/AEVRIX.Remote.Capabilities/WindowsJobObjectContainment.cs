using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Aevrix.Remote.Capabilities;

public sealed record WindowsJobObjectPolicy(
    long MaximumProcessMemoryBytes,
    int MaximumActiveProcesses = 1)
{
    public WindowsJobObjectPolicy Validate()
    {
        if (MaximumProcessMemoryBytes is < 16_777_216 or > 68_719_476_736)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumProcessMemoryBytes));
        }

        if (MaximumActiveProcesses is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumActiveProcesses));
        }

        return this;
    }
}

internal sealed class WindowsJobObjectLease : IDisposable
{
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    private readonly SafeJobHandle _handle;
    private bool _disposed;

    private WindowsJobObjectLease(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public static WindowsJobObjectLease CreateAndAssign(Process process, WindowsJobObjectPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Job Object containment requires Windows.");
        }

        var handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the governed Windows Job Object.");
        }

        try
        {
            Configure(handle, policy);
            if (!NativeMethods.AssignProcessToJobObject(handle, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the adapter process to the governed Windows Job Object.");
            }

            return new WindowsJobObjectLease(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void Configure(SafeJobHandle handle, WindowsJobObjectPolicy policy)
    {
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
                    | JobObjectLimitActiveProcess
                    | JobObjectLimitProcessMemory,
                ActiveProcessLimit = checked((uint)policy.MaximumActiveProcesses)
            },
            ProcessMemoryLimit = checked((UIntPtr)(ulong)policy.MaximumProcessMemoryBytes)
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!NativeMethods.SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformation,
                    buffer,
                    checked((uint)size)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure governed Windows Job Object limits.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static partial class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int jobObjectInformationClass,
            IntPtr jobObjectInformation,
            uint jobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
